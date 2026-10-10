namespace TOLIK.Contracts;

// ─── CU-1: Desktop Awareness / PC State (docs/computer-use.md §2) ───

/// <summary>One observed window (deterministic agent data — Win32 EnumWindows, already M8).</summary>
public sealed record PcWindowState(
    string WindowId,
    string Title,
    string ProcessName,
    bool Focused,
    bool Minimized,
    int MonitorIndex,
    UiBounds Bounds)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(WindowId) && !string.IsNullOrWhiteSpace(ProcessName);
}

/// <summary>Per-application aggregation (running, window ids, notification count).</summary>
public sealed record PcApplicationState(
    string ProcessName,
    bool Running,
    IReadOnlyList<string> WindowIds,
    int NotificationCount);

/// <summary>
/// PC State (CU-1 Desktop Awareness): Applications/Windows/Focused/Notifications/
/// Running/Monitors — the deterministic desktop model commands like «верни то окно»
/// and «что вчера смотрели» work from. Source: the agent's window/process observations;
/// periodic observation (NOT every frame into an LLM). Additive to WorldState (which
/// stays the perception/world model) — this is the PC-specific extension.
/// </summary>
public sealed record PcState(
    IReadOnlyList<PcWindowState> Windows,
    IReadOnlyList<PcApplicationState> Applications,
    string? FocusedWindowId,
    int MonitorCount,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The focused window, if any (routing only — NEVER an authorization input).</summary>
    public PcWindowState? FocusedWindow => Windows.FirstOrDefault(w => w.WindowId == FocusedWindowId);

    public static PcState Empty { get; } = new([], [], null, 0, DateTimeOffset.UtcNow);
}

/// <summary>
/// Deterministic PC-state aggregation (the PerceptionService.Apply pattern): merges
/// agent window observations into the current PC state — focused/notifications/running
/// derived deterministically, never guessed. Thread-safe. The live wiring (periodic
/// capture loop) is post-Genesis CU-3+; pre-Genesis this is a read-only model.
/// </summary>
public sealed class PcStateBuilder
{
    private readonly object _gate = new();
    private PcState _state = PcState.Empty;

    public PcState Current { get { lock (_gate) { return _state; } } }

    /// <summary>Applies one window snapshot from the agent: replaces the window list,
    /// derives per-application running state and the focused window deterministically.</summary>
    public PcState Apply(IReadOnlyList<PcWindowState> windows, int monitorCount)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (monitorCount < 0) throw new ArgumentOutOfRangeException(nameof(monitorCount));
        var focused = windows.FirstOrDefault(w => w.Focused)?.WindowId;
        var applications = windows
            .GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PcApplicationState(
                g.Key,
                Running: true,
                g.Select(w => w.WindowId).ToList(),
                NotificationCount: 0))
            .ToList();
        var next = new PcState(windows, applications, focused, monitorCount, DateTimeOffset.UtcNow);
        lock (_gate) { _state = next; }
        return next;
    }

    /// <summary>Updates the notification count for one application (the agent's tray/notification data).</summary>
    public void SetNotifications(string processName, int count)
    {
        if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("Process name is required.", nameof(processName));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            var applications = _state.Applications
                .Select(a => a.ProcessName == processName ? a with { NotificationCount = count } : a)
                .ToList();
            _state = _state with { Applications = applications, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }
}

// ─── CU-2: UI Observation (docs/computer-use.md §3, треб. #4/#5) ───

/// <summary>Screen-space rectangle.</summary>
public sealed record UiBounds(int X, int Y, int Width, int Height);

public static class UiElementRole
{
    public const string Button = "button";
    public const string Field = "field";
    public const string Tab = "tab";
    public const string Menu = "menu";
    public const string ListItem = "list_item";
    public const string Dialog = "dialog";
    public const string Window = "window";
    public const string Text = "text";
}

public static class UiElementState
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Selected = "selected";
    public const string Loading = "loading";
    public const string Error = "error";
}

/// <summary>
/// One UI element (CU-2): {text, role, automation_id, bounds} — better than
/// click(812,731): semantic targets are primary, coordinates are detail.
/// </summary>
public sealed record UiElement(
    string? Text,
    string Role,
    string? AutomationId,
    UiBounds? Bounds,
    string State = UiElementState.Enabled)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Role);
}

/// <summary>One window's UI representation from all sources (a11y tree + OCR + vision metadata).</summary>
public sealed record UiRepresentation(
    string WindowId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<UiElement> Elements)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(WindowId) && Elements.All(e => e.IsValid);
}

/// <summary>
/// UI observer contract (CU-2): Observation → UI Representation. NOT coordinates —
/// the semantic target model is primary. Provider-replaceable (§6 директивы): local
/// OCR (Windows.Media.Ocr — built-in) / UIA — cheap; a vision model (cloud
/// qwen2.5vl, already in the stack) — for the complex; never hard-wired. Pre-Genesis:
/// the contract only — providers land with CU-3+ (actions are post-Genesis).
/// </summary>
public interface IUiObserver
{
    /// <summary>Observes one window and returns its UI representation.</summary>
    Task<UiRepresentation> ObserveAsync(string windowId, CancellationToken cancellationToken = default);
}
