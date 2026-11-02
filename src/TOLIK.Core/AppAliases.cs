namespace TOLIK.Core;

/// <summary>
/// Maps a spoken or typed application name to a Windows executable.
///
/// Intent normalization, not execution: it turns «калькулятор» into <c>calc.exe</c> so
/// the deterministic OpenApp intent can hand a concrete target to the Agent. It decides
/// nothing about permissions — the Agent call is still authorized separately — and an
/// unknown name is passed through unchanged, so a real executable path keeps working.
///
/// This lives in Core rather than in the host's Program.cs because it is a pure mapping
/// with user-visible consequences: the Russian names were corrupted once already
/// (literals stored as mojibake), which made every Russian app name fall through
/// unmatched while the English aliases kept working. A table nothing can test is a
/// table that rots.
/// </summary>
public static class AppAliases
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["калькулятор"] = "calc.exe",
        ["calculator"] = "calc.exe",
        ["блокнот"] = "notepad.exe",
        ["notepad"] = "notepad.exe",
        ["пейнт"] = "mspaint.exe",
        ["паинт"] = "mspaint.exe",
        ["paint"] = "mspaint.exe",
        ["рисовалка"] = "mspaint.exe",
        ["проводник"] = "explorer.exe",
        ["explorer"] = "explorer.exe",
        ["файлы"] = "explorer.exe",
        ["браузер"] = "msedge.exe",
        ["интернет"] = "msedge.exe",
        ["browser"] = "msedge.exe",
        ["терминал"] = "cmd.exe",
        ["консоль"] = "cmd.exe",
        ["cmd"] = "cmd.exe",
        ["командная строка"] = "cmd.exe",
        ["powershell"] = "powershell.exe",
        ["провера"] = "powershell.exe",
        ["диспетчер задач"] = "taskmgr.exe",
        ["task manager"] = "taskmgr.exe",
        ["настройки"] = "ms-settings:",
        ["параметры"] = "ms-settings:",
        ["settings"] = "ms-settings:",
    };

    /// <summary>
    /// Returns the executable for a known alias, or the trimmed input unchanged.
    /// Empty and whitespace-only names return an empty string — the caller reports that
    /// rather than launching something arbitrary.
    /// </summary>
    public static string Resolve(string name)
    {
        if (name is null)
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return Map.TryGetValue(trimmed, out string? executable) ? executable : trimmed;
    }
}
