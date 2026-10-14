namespace TOLIK.Contracts;

/// <summary>
/// Single source of truth for bounded task-step syntax ("capability:argument").
/// The execute endpoint dispatches these; the LLM planner offers only these.
/// Unknown actions fail closed at execute time.
/// </summary>
public static class AgentStepSyntax
{
    public const string ReadFile = "read_file:";
    public const string ListDirectory = "list_directory:";
    public const string GetProcessStatus = "get_process_status:";
    public const string RunProcess = "run_process:";
    public const string RunCodex = "run_codex:";
    public const string RunOpenCode = "run_opencode:";
    public const string RunBuild = "run_build:";
    public const string RunTests = "run_tests:";
    public const string GuiRead = "gui_read:";
    public const string GuiClick = "gui_click:";

    public static readonly IReadOnlyList<string> Prefixes =
    [
        ReadFile, ListDirectory, GetProcessStatus, RunProcess,
        RunCodex, RunOpenCode, RunBuild, RunTests, GuiRead, GuiClick
    ];
}
