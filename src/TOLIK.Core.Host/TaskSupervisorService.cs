using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Infrastructure.Tasks;

namespace TOLIK.Core.Host;

/// <summary>
/// The per-task supervisor decision (docs/continuous-autonomy.md CA-2 — PLAN REFORM
/// PRG-3a): the bounded escalation ladder for a stalled RUNNING task. PURE —
/// deterministic given (lastSeen, now, config), unit-testable without a DB.
/// </summary>
internal sealed record SupervisorDecision(int Level, bool JournalMind, bool NotifyOwner, bool Pause)
{
    public static SupervisorDecision None { get; } = new(0, false, false, false);
}

/// <summary>
/// TaskSupervisorService (docs/continuous-autonomy.md CA-2 — PLAN REFORM PRG-3a):
/// a watchdog BackgroundService over the RUNNING tasks — heartbeat/liveness/stall
/// detection. The liveness = the last lifecycle activity (the in-memory event
/// tracking) with the task's CreatedAt as the honest persisted floor (a restart
/// never falsely stalls a task — the unknown reads as fresh). The bounded
/// escalation ladder (NEVER auto-cancel — the owner decides; NEVER an infinite
/// loop — the levels are capped): L1 the mind journal (TOLIK notices), L2 the WS
/// notification to the task's owner (per-user routing, §19/§50), L3 the PAUSE via
/// the existing lifecycle (stop + preserve + ask — the EscalatePauseAndAsk
/// semantics, CA-6). The stall counters reset the moment real activity returns.
/// §40: the loop never dies silently. Read-only observation + the bounded pause —
/// it never authorizes anything new.
/// </summary>
public sealed class TaskSupervisorService(
    IDbContextFactory<TolikDbContext> dbFactory,
    TaskLifecycle lifecycle,
    RealtimeEventHub hub,
    MindRuntime mind,
    ILogger<TaskSupervisorService> logger) : BackgroundService
{
    internal const string TickSecondsVariable = "TOLIK_CA_SUPERVISOR_SECONDS";
    internal const string StallMinutesVariable = "TOLIK_CA_STALL_MINUTES";
    internal const string PauseLevelVariable = "TOLIK_CA_STALL_PAUSE_TICKS";
    private const double DefaultTickSeconds = 30.0;
    private const double DefaultStallMinutes = 10.0;
    private const int DefaultPauseLevel = 3;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSeen = new();
    private readonly ConcurrentDictionary<Guid, int> _stalledTicks = new();

    public static double TickSeconds =>
        double.TryParse(Environment.GetEnvironmentVariable(TickSecondsVariable), out var v) && double.IsFinite(v) && v >= 5.0 ? v : DefaultTickSeconds;
    public static double StallMinutes =>
        double.TryParse(Environment.GetEnvironmentVariable(StallMinutesVariable), out var v) && double.IsFinite(v) && v >= 1.0 ? v : DefaultStallMinutes;
    public static int PauseLevel =>
        int.TryParse(Environment.GetEnvironmentVariable(PauseLevelVariable), out var v) && v >= 2 && v <= 10 ? v : DefaultPauseLevel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Task supervisor started: tick {Tick:0.#}s, stall {Stall:0.#}min, pause at level {Level}.", TickSeconds, StallMinutes, PauseLevel);
        // The lifecycle events refresh the liveness (TaskProgress/ToolStarted/... — any real activity).
        lifecycle.EventPublished += ev => _lastSeen[ev.TaskId] = ev.OccurredAt;
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Task supervisor tick failed; continuing."); // §40
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Task supervisor stopped.");
    }

    private async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The RUNNING tasks (the metadata only: no descriptions in the supervisor's scope).
        List<(Guid Id, Guid UserId, DateTimeOffset CreatedAt)> running;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            running = await db.Tasks.AsNoTracking()
                .Where(x => x.Status == "Running")
                .Select(x => new ValueTuple<Guid, Guid, DateTimeOffset>(x.Id, x.UserId, x.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        var stall = TimeSpan.FromMinutes(StallMinutes);
        var pauseLevel = PauseLevel;
        var liveIds = new HashSet<Guid>();
        foreach (var (taskId, userId, createdAt) in running)
        {
            liveIds.Add(taskId);
            // The honest liveness: the tracked activity or the task's creation (a restart
            // never falsely stalls — the unknown reads as fresh; §50: never fabricated).
            var lastSeen = createdAt;
            if (_lastSeen.TryGetValue(taskId, out var seen) && seen > lastSeen) lastSeen = seen;
            var ticks = _stalledTicks.GetValueOrDefault(taskId, 0);
            var decision = Decide(lastSeen, now, stall, ticks, pauseLevel);
            switch (decision)
            {
                case { Level: 0 }:
                    _stalledTicks.TryRemove(taskId, out _); // the activity returned — reset
                    break;
                case { Pause: true }:
                    logger.LogWarning("Task {TaskId} stalled for {Ticks} supervisor ticks — pausing (stop + preserve + ask).", taskId, ticks + 1);
                    mind.OnTaskStalled();
                    lifecycle.Pause(taskId, "stall_timeout"); // the existing state machine: Running → Paused
                    hub.PublishToTaskOwner(taskId, System.Text.Json.JsonSerializer.Serialize(new { TaskId = taskId, Kind = "TaskStalledPaused", Detail = "stall_timeout", OccurredAt = now }));
                    _stalledTicks.TryRemove(taskId, out _);
                    break;
                case { NotifyOwner: true }:
                    logger.LogWarning("Task {TaskId} stalled (tick {Ticks}) — notifying the owner.", taskId, ticks + 1);
                    mind.OnTaskStalled();
                    hub.PublishToTaskOwner(taskId, System.Text.Json.JsonSerializer.Serialize(new { TaskId = taskId, Kind = "TaskStalled", Detail = $"no activity for {StallMinutes:0.#} min", OccurredAt = now }));
                    _stalledTicks[taskId] = ticks + 1;
                    break;
                case { JournalMind: true }:
                    logger.LogWarning("Task {TaskId} stalled (tick {Ticks}) — the mind notices.", taskId, ticks + 1);
                    mind.OnTaskStalled();
                    _stalledTicks[taskId] = ticks + 1;
                    break;
            }
        }

        // Bounded memory: the state of the no-longer-running tasks is pruned.
        foreach (var id in _lastSeen.Keys)
            if (!liveIds.Contains(id)) { _lastSeen.TryRemove(id, out _); _stalledTicks.TryRemove(id, out _); }
        foreach (var id in _stalledTicks.Keys)
            if (!liveIds.Contains(id)) _stalledTicks.TryRemove(id, out _);
    }

    /// <summary>The PURE ladder (unit-tested): L0 none; L1 the mind journal; L2 + the
    /// owner notification; L>=PauseLevel the pause (stop + preserve + ask — never
    /// auto-cancel: the owner decides after the pause).</summary>
    internal static SupervisorDecision Decide(DateTimeOffset lastSeen, DateTimeOffset now, TimeSpan stall, int stalledTicks, int pauseLevel)
    {
        if (now - lastSeen <= stall)
            return SupervisorDecision.None; // alive — and the caller resets the counter
        var level = stalledTicks + 1; // this tick's stall level (1-based)
        if (level >= pauseLevel)
            return new SupervisorDecision(level, JournalMind: true, NotifyOwner: true, Pause: true);
        if (level >= 2)
            return new SupervisorDecision(level, JournalMind: true, NotifyOwner: true, Pause: false);
        return new SupervisorDecision(level, JournalMind: true, NotifyOwner: false, Pause: false);
    }
}
