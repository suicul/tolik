using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Infrastructure.Tasks;

[assembly: InternalsVisibleTo("TOLIK.Foundation.Tests")]

namespace TOLIK.Core.Host;

/// <summary>
/// Per-user state primitives gathered by the State Watcher for ONE connected owner
/// (privacy §19/§50: only that user's own task state — nothing from anyone else).
/// </summary>
/// <param name="RunningTasks">Count of this user's tasks in the persisted "Running" status.</param>
/// <param name="TaskProgress">Evidence-derived progress [0..1] of the running work (CA-4 snapshot; 0 when none).</param>
internal sealed record CephalonUserSnapshot(int RunningTasks, double TaskProgress)
{
    public static CephalonUserSnapshot Empty { get; } = new(0, 0.0);
}

/// <summary>
/// Cephalon Expression State Watcher (docs/cephalon-desktop.md CE-2): a BackgroundService
/// that ticks at 5 Hz (configurable), gathers EACH connected owner's REAL state, composes
/// the expression with the existing pure <see cref="ExpressionComposer"/> (never
/// re-implemented) and broadcasts it over the WebSocket hub — ONLY when the frame changed
/// meaningfully or the heartbeat elapsed (no identical-frame spam).
///
/// Wiring honesty (§50 — never fabricated data):
///   • tasks: persisted "Running" rows + the lifecycle's evidence-derived progress — wired;
///   • locked: the same <see cref="CoreSecurityState.IsLocked"/> the /health endpoint uses — wired;
///   • voice: an in-flight voice turn (VoiceTurnRegistry) → IsSpeaking — wired COARSELY;
///     amplitude/frequency bands stay honest zeros (no cheap audio telemetry in Core —
///     the CE-3 remainder: wire the real TTS spectrum when the voice service exposes it);
///   • Mind state: WIRED (PLAN REFORM PRG-1a, 12.10.2026 — pre-Genesis by the owner's
///     directive): the MindRuntime's real drives (curiosity/frustration/satisfaction→
///     confidence/attention from real interactions + task outcomes) flow into the
///     emotional fields; Amusement/Uncertainty/Thinking stay honest zeros until the
///     humor/planner tracks land;
///   • Recovery/SignalLevel: no real source exists in Core yet — honest zeros.
///
/// Security invariants (SECURITY & TRUST MODEL): the service is READ-ONLY state
/// observation + broadcast — it never authorizes anything, never logs secrets, and the
/// payload carries ONLY the expression fields (no task details, no user data beyond the
/// per-user routing itself).
/// </summary>
public sealed class CephalonExpressionService(
    IDbContextFactory<TolikDbContext> dbFactory,
    RealtimeEventHub hub,
    TaskLifecycle lifecycle,
    CoreSecurityState securityState,
    VoiceTurnRegistry voiceTurns,
    TOLIK.Core.MindRuntime mind,
    TOLIK.Core.VoiceSpectrumState spectrum,
    TOLIK.Core.NotificationGate notifications,
    ILogger<CephalonExpressionService> logger) : BackgroundService
{
    internal const string WireKind = "cephalon_expression";
    internal const string BroadcastHzVariable = "TOLIK_CEPHALON_BROADCAST_HZ";
    internal const string HeartbeatSecondsVariable = "TOLIK_CEPHALON_HEARTBEAT_SECONDS";
    private const double DefaultHz = 5.0;
    private const double DefaultHeartbeatSeconds = 1.0;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<Guid, ExpressionOutput> _previous = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPublish = new();
    private readonly TimeSpan _tickInterval = TickIntervalFromConfig();
    private readonly TimeSpan _heartbeat = HeartbeatFromConfig();
    // MI-7: the daily proactive budget state (mind-side; the E2 gate adds the rate windows).
    private (DateTimeOffset Day, int Count) _proactiveDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Cephalon expression service started: {Hz:0.##} Hz, heartbeat {Seconds:0.##}s.",
            1.0 / _tickInterval.TotalSeconds, _heartbeat.TotalSeconds);

        // Brief settle delay: let migrations and the first WS clients connect before
        // the first pass (matches the other background services' pattern, shorter —
        // the expression is realtime).
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_tickInterval);
        var lastTick = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var dt = (now - lastTick).TotalSeconds;
            lastTick = now;
            try
            {
                await TickAsync(dt, now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // §40: the loop never dies silently — log and keep ticking.
                logger.LogWarning(ex, "Cephalon expression tick failed; continuing.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Cephalon expression service stopped.");
    }

    private async Task TickAsync(double deltaSeconds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The mind's security input flows even when nobody is connected (the mind lives
        // between conversations — MI-3's Safety drive + the LOCKED catalog restriction).
        mind.MarkSecurityState(securityState.IsLocked);
        var userIds = hub.ConnectedUserIds;
        if (userIds.Count == 0) return; // nobody listening — zero work (and quiet tests)
        var locked = securityState.IsLocked;

        // Gather: ONE DbContext per tick; per-user queries inside (§19/§50 scoping).
        var gathered = new List<(Guid UserId, CephalonUserSnapshot Snapshot)>(userIds.Count);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            foreach (var userId in userIds)
                gathered.Add((userId, await GatherUserAsync(db, userId, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Degraded pass (§40): DB unavailable → compose honest idle for everyone
            // (never a fake "working" expression). The memory-side signals (locked,
            // voice turn) remain real — only the task store degrades.
            logger.LogWarning(ex, "Cephalon expression task-state gather failed; composing degraded idle.");
            gathered = userIds.Select(u => (u, CephalonUserSnapshot.Empty)).ToList();
        }

        // CE-3: the real outgoing TTS spectrum — ONE reading per tick (the singleton
        // voice; the honest zeros during the pauses are the spectrum's own rule).
        var voice = spectrum.Current(now);
        // CE-5: the active DSL recipe — ONE reading per tick; its validated deltas
        // blend additively into every composed expression (the one-blended principle).
        var activeRecipe = mind.ExpressionRecipes.ActiveRecipe;
        foreach (var (userId, snapshot) in gathered)
        {
            var previous = _previous.TryGetValue(userId, out var prev) ? prev : null;
            if (previous is null)
            {
                // CA-5 (PRG-3b — absence/return): the (re)connected owner gets the
                // proactive «what happened» summary; the mind's social drive rises (a
                // REAL presence event). UserDisconnected ≠ TaskCancelled: the tasks
                // live Core-side and the supervisor watches them regardless of presence.
                mind.OnUserReturned();
                hub.PublishToUser(userId, BuildPresenceReturnJson(mind.Journal.RenderRecent(TimeSpan.FromHours(1), now)));
            }
            // MI-7: the mind's self-awareness — the failure pattern (3+ consecutive
            // failures) surfaces ONCE per streak, through BOTH gates: the mind-side
            // ProactivePolicy (quiet window + budget + locked) and the E2 NotificationGate
            // (the fixed whitelist + the rate windows). The mind proposes, the gates decide.
            TrySurfaceFailurePattern(userId, now, locked);
            var lastPublished = _lastPublish.TryGetValue(userId, out var at) ? at : DateTimeOffset.MinValue;
            // The mind is ONE for the ONE TOLIK (identity-level, not per-user): its real
            // drives flow into every connected owner's expression (PLAN REFORM PRG-1a).
            var emotions = mind.ExpressionEmotions(now);
            var output = ComposeFor(snapshot, locked, voiceTurns.HasInFlightTurn(userId), connected: true, emotions, voice, activeRecipe, previous, deltaSeconds);

            _previous[userId] = output;
            if (!ShouldPublish(previous, output, now - lastPublished, _heartbeat)) continue;
            _lastPublish[userId] = now;
            hub.PublishToUser(userId, BuildWireJson(output, now));
        }

        // Prune state of users who disconnected (bounded per-user memory).
        if (_previous.Count != userIds.Count)
        {
            var live = new HashSet<Guid>(userIds);
            foreach (var key in _previous.Keys)
                if (!live.Contains(key)) { _previous.TryRemove(key, out _); _lastPublish.TryRemove(key, out _); }
        }
    }

    /// <summary>THIS user's real task state only — the query is scoped by UserId (privacy §19/§50).</summary>
    private async Task<CephalonUserSnapshot> GatherUserAsync(TolikDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var running = await db.Tasks.AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == "Running")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (running.Count == 0) return CephalonUserSnapshot.Empty;

        // Honest progress (§50, CA-4): the lifecycle's evidence-derived snapshot —
        // 0 when no outcomes are observed, never fabricated; the newest running task
        // that carries one wins.
        double progress = 0;
        foreach (var taskId in running)
        {
            var p = lifecycle.GetProgress(taskId);
            if (p is null) continue;
            progress = Math.Max(progress, p.OverallPercent / 100.0);
            break;
        }
        return new CephalonUserSnapshot(running.Count, progress);
    }

    // ─────────────────────────── Pure, testable core ───────────────────────────

    /// <summary>
    /// The honest state mapping (§50): every field below traces to a REAL source or
    /// stays at its honest zero. The composer's own normalization then clamps garbage.
    /// Mind wiring (PLAN REFORM PRG-1a, 12.10.2026): the MindRuntime's real drives
    /// FLOW — curiosity/frustration/satisfaction→confidence/attention. Amusement/
    /// Uncertainty/Thinking have no real source yet (humor/planner tracks) — honest zeros.
    /// </summary>
    internal static ExpressionState ToExpressionState(
        CephalonUserSnapshot snapshot, bool locked, bool turnInFlight, bool connected,
        MindEmotions emotions, VoiceLevels? voice = null)
    {
        var processing = snapshot.RunningTasks > 0 ? 1.0 : 0.0;
        return new ExpressionState(
            // Cognitive: attention = the real interaction focus (decays ~10 min);
            // thinking has no planner source in Core yet — honest 0.
            Attention: emotions.Attention, Thinking: 0,
            Concentration: processing, // a running task IS the current focus
            // Emotional (Mind MI-0..4 WIRED per PRG-1a/1d): the real mind drives flow —
            // curiosity/frustration/confidence/attention/uncertainty; amusement/thinking
            // stay honest zeros until the humor/planner tracks land.
            Amusement: 0, Curiosity: emotions.Curiosity, Frustration: emotions.Frustration,
            Confidence: emotions.Confidence, Uncertainty: emotions.Uncertainty,
            // Social presence: the owner has a live authenticated WS connection.
            Engagement: connected ? 1.0 : 0.0,
            // Voice: the turn lifecycle (IsSpeaking) + the REAL outgoing TTS spectrum
            // (CE-3, PRG-2): the WAV-path audio feeds the amplitude/bands — the body
            // reacts to its own voice; the MP3 fallback and the pauses read as zeros
            // (the spectrum's own honest-silence rule — never a fake waveform).
            IsSpeaking: turnInFlight,
            Amplitude: voice?.Amplitude ?? 0, LowBand: voice?.Low ?? 0,
            MidBand: voice?.Mid ?? 0, HighBand: voice?.High ?? 0,
            // System alarms: no real alarm source in Core yet — honest None (never fake).
            SignalLevel: SystemSignalLevel.None,
            Processing: processing,
            TaskProgress: snapshot.TaskProgress,
            Recovery: 0, // no recovery source in Core yet — honest zero
            Locked: locked);
    }

    /// <summary>Per-user composition: the existing pure composer does ALL the math.
    /// The active DSL recipe (CE-5) blends ADDITIVELY into the state pre-composer —
    /// another influence, never an override; a null recipe changes nothing.</summary>
    internal static ExpressionOutput ComposeFor(
        CephalonUserSnapshot snapshot, bool locked, bool turnInFlight, bool connected,
        MindEmotions emotions, VoiceLevels? voice, ExpressionRecipe? activeRecipe,
        ExpressionOutput? previous, double deltaSeconds) =>
        ExpressionComposer.Compose(
            RecipeApplication.Apply(
                ToExpressionState(snapshot, locked, turnInFlight, connected, emotions, voice),
                activeRecipe).Normalized(),
            previous, deltaSeconds);

    /// <summary>
    /// Publish gating: the first frame, a mode switch, any parameter moving more than
    /// 0.01, or the heartbeat elapsed — everything else stays silent (no WS spam).
    /// A non-finite candidate never broadcasts (fail closed, §18).
    /// </summary>
    internal static bool ShouldPublish(
        ExpressionOutput? previous, ExpressionOutput candidate, TimeSpan sinceLastPublish, TimeSpan heartbeat)
    {
        if (!candidate.IsFinite) return false;
        if (previous is null) return true;
        if (previous.Mode != candidate.Mode) return true;
        if (sinceLastPublish >= heartbeat) return true;
        return MaxDelta(previous, candidate) > 0.01;
    }

    private static double MaxDelta(ExpressionOutput a, ExpressionOutput b)
    {
        double[] deltas =
        [
            Math.Abs(a.EyeScale - b.EyeScale),
            Math.Abs(a.EyeOpenness - b.EyeOpenness),
            Math.Abs(a.EyeRotation - b.EyeRotation),
            Math.Abs(a.Asymmetry - b.Asymmetry),
            Math.Abs(a.ShellExpansion - b.ShellExpansion),
            Math.Abs(a.ShellDeformation - b.ShellDeformation),
            Math.Abs(a.Rotation - b.Rotation),
            Math.Abs(a.PulseAmplitude - b.PulseAmplitude),
            Math.Abs(a.GlowIntensity - b.GlowIntensity),
            Math.Abs(a.ParticleDensity - b.ParticleDensity),
            Math.Abs(a.SpectrumIntensity - b.SpectrumIntensity),
            Math.Abs(a.MovementSpeed - b.MovementSpeed),
            Math.Abs(a.AttentionAngle - b.AttentionAngle),
            Math.Abs(a.AttentionIntensity - b.AttentionIntensity),
            Math.Abs(a.TransitionEnergy - b.TransitionEnergy),
        ];
        return deltas.Max();
    }

    /// <summary>The fixed wire frame (the desktop/Android lanes consume EXACTLY this):
    /// <summary>
    /// CA-5 (PRG-3b): the return-presentation payload — a proactive «what happened»
    /// summary pushed to the owner on their (re)connect. The summary comes from the
    /// journal (privacy by construction: fixed kinds + generic summaries only — safe
    /// for any authenticated reader). Unknown to a renderer = safely ignored.
    /// </summary>
    internal static string BuildPresenceReturnJson(string summary) =>
        JsonSerializer.Serialize(new { kind = "presence_return", summary });

    /// <summary>
    /// MI-7 (PRG-5a): the failure-pattern proactive surface — the mind NOTICED a streak
    /// of consecutive failures and wants to tell the owner. The message is composed by
    /// the mind (honest, deterministic, no task details — privacy by construction), then
    /// gated TWICE: (1) the mind-side <see cref="ProactivePolicy"/> (the daily budget is
    /// hard, the quiet window after a real interaction, LOCKED silences everything) and
    /// (2) the E2 <see cref="NotificationGate"/> (the fixed whitelist + the rate windows).
    /// A gate denial keeps the surface pending (nothing consumed); a sent message is
    /// journaled and budgeted. A dropped-by-E2 message is an honest loss (the journal
    /// already carries every failure — nothing informational is lost).
    /// </summary>
    internal void TrySurfaceFailurePattern(Guid userId, DateTimeOffset now, bool locked)
    {
        if (!mind.ShouldSurfaceFailurePattern) return;

        // 1. The mind-side gate (pure — no side effects on denial).
        var day = now.Date;
        var proactivesToday = _proactiveDay.Day == day ? _proactiveDay.Count : 0;
        var decision = ProactivePolicy.Decide(
            ProactivePolicy.ProactiveKind.SystemSignal,
            mind.Snapshot(),
            mind.MinutesSinceInteraction(now),
            proactivesToday,
            now,
            locked);
        if (!decision.Allowed) return;

        // 2. Consume the pending surface (once per streak).
        var message = mind.ConsumeFailurePatternSurface();
        if (message is null) return;

        // 3. The E2 gate — the final gate (the fixed whitelist + the rate windows).
        var verdict = notifications.TrySend(NotificationKind.TaskFailed, now);
        if (!verdict.Allowed)
        {
            logger.LogInformation(
                "Failure-pattern surface dropped honestly by the notification gate: {Reason}.", verdict.Reason);
            return;
        }

        // 4. Send + budget + journal (the journal entry is already recorded by the consume).
        hub.PublishToUser(userId, BuildProactiveJson(message));
        _proactiveDay = (day, proactivesToday + 1);
        logger.LogInformation("Proactive failure-pattern surfaced to {User} ({Today}/{Budget}).",
            userId, proactivesToday + 1, ProactivePolicy.DailyBudget);
    }

    /// <summary>The proactive payload — the same tolerant wire shape as presence_return
    /// (unknown to a renderer = safely ignored).</summary>
    internal static string BuildProactiveJson(string summary) =>
        JsonSerializer.Serialize(new { kind = "proactive", summary });

    /// <summary>
    /// {"kind":"cephalon_expression","expression":{...camelCase...},"at":"<ISO-8601>"}.
    /// The expression fields are serialized EXPLICITLY — exactly the 16 contract
    /// parameters (the record's computed IsFinite diagnostic property must never leak
    /// onto the wire), and the shared Contracts type stays untouched.</summary>
    internal static string BuildWireJson(ExpressionOutput output, DateTimeOffset at) =>
        JsonSerializer.Serialize(new
        {
            kind = WireKind,
            expression = new
            {
                output.EyeScale,
                output.EyeOpenness,
                output.EyeRotation,
                output.Asymmetry,
                output.ShellExpansion,
                output.ShellDeformation,
                output.Rotation,
                output.PulseAmplitude,
                output.GlowIntensity,
                output.ParticleDensity,
                output.SpectrumIntensity,
                output.MovementSpeed,
                output.AttentionAngle,
                output.AttentionIntensity,
                output.TransitionEnergy,
                mode = output.Mode
            },
            at
        }, WireOptions);

    // ───────────────────── Fail-closed configuration (§41) ─────────────────────

    internal static TimeSpan TickIntervalFromConfig(string? raw = null)
    {
        var hz = ParsePositiveFinite(raw ?? Environment.GetEnvironmentVariable(BroadcastHzVariable), DefaultHz);
        // Floor 10 ms (100 Hz max): a degenerate value must never busy-loop the core.
        return TimeSpan.FromSeconds(Math.Max(1.0 / hz, 0.01));
    }

    internal static TimeSpan HeartbeatFromConfig(string? raw = null) =>
        TimeSpan.FromSeconds(ParsePositiveFinite(
            raw ?? Environment.GetEnvironmentVariable(HeartbeatSecondsVariable), DefaultHeartbeatSeconds));

    private static double ParsePositiveFinite(string? raw, double fallback) =>
        double.TryParse(raw, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0.0
            ? v
            : fallback;
}
