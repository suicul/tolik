using System.Threading.Channels;

namespace TOLIK.Core;

/// <summary>
/// MindEvent (docs/mind.md §6, MI-1): the INTERNAL event bus — state updates feeding
/// deterministic evaluation (never `while(true) askLLM`). SEPARATE from the client-facing
/// RealtimeEventHub: mind events update internal state and may generate goals; arbitrary
/// external events NEVER trigger privileged actions — everything still passes the
/// authorization (mind §6/§12: Mind → Proposal → AutonomyPolicy → Authorization →
/// Capability → Execution, never Mind → PowerShell).
/// </summary>
public sealed record MindEvent(
    string Kind,
    string? Detail,
    DateTimeOffset OccurredAt)
{
    public static MindEvent Of(string kind, string? detail = null) => new(kind, detail, DateTimeOffset.UtcNow);
}

public static class MindEventKind
{
    public const string UserSpoke = "user_spoke";
    public const string UserLeft = "user_left";
    public const string UserReturned = "user_returned";
    public const string TaskCompleted = "task_completed";
    public const string TaskFailed = "task_failed";
    public const string SystemHealthChanged = "system_health_changed";
    public const string ApplicationOpened = "application_opened";
    public const string ApplicationChanged = "application_changed";
    public const string SkillFailed = "skill_failed";
    public const string NewInformation = "new_information";
    public const string TimerElapsed = "timer_elapsed";
    public const string GoalProgressChanged = "goal_progress_changed";
    public const string BoredomThresholdCrossed = "boredom_threshold_crossed";
    public const string CuriosityTriggered = "curiosity_triggered";
    public const string ResourceAvailabilityChanged = "resource_availability_changed";
}

/// <summary>
/// In-process mind event channel (docs/mind.md §6, MI-1): bounded (drop-oldest under
/// pressure — a lost mind event costs nothing), publishing NEVER blocks. SINGLE-CONSUMER
/// semantics: multiple readers compete (each event goes to exactly one); multicast
/// fan-out to several subscribers is a post-Genesis wiring decision (MI-1 consumers
/// land then) — do not assume fan-out. The deterministic handlers do the state updates;
/// the LLM reasoning happens only when a handler explicitly needs it (never in the loop).
/// </summary>
public sealed class MindEventBus
{
    public const int QueueCapacity = 256;

    private readonly Channel<MindEvent> _channel = Channel.CreateBounded<MindEvent>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool TryPublish(MindEvent @event) => _channel.Writer.TryWrite(@event);

    public ChannelReader<MindEvent> Reader => _channel.Reader;
}
