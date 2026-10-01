namespace TOLIK.Agent;

public sealed class AgentRuntime
{
    private AgentRuntime(bool isEnrolled)
    {
        IsEnrolled = isEnrolled;
    }

    public bool IsEnrolled { get; }

    public bool ToolsEnabled => false;

    public static AgentRuntime CreateUnenrolled() => new(isEnrolled: false);

    public string StartupMessage() =>
        $"TOLIK.Agent started. Enrollment={IsEnrolled.ToString().ToLowerInvariant()}; toolsEnabled={ToolsEnabled.ToString().ToLowerInvariant()}.";
}
