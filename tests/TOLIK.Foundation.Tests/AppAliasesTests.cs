using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Application-name normalization (intent OpenApp). The Russian aliases were once
/// stored as mojibake after a UTF-8/CP1251 round trip: the table compiled, the English
/// aliases worked, and every Russian name silently fell through — a failure that is
/// invisible in logs and only shows up as TOLIK not understanding its owner.
/// </summary>
public sealed class AppAliasesTests
{
    [Theory]
    [InlineData("калькулятор", "calc.exe")]
    [InlineData("Калькулятор", "calc.exe")]
    [InlineData("  калькулятор  ", "calc.exe")]
    [InlineData("блокнот", "notepad.exe")]
    [InlineData("пейнт", "mspaint.exe")]
    [InlineData("паинт", "mspaint.exe")]
    [InlineData("рисовалка", "mspaint.exe")]
    [InlineData("проводник", "explorer.exe")]
    [InlineData("файлы", "explorer.exe")]
    [InlineData("браузер", "msedge.exe")]
    [InlineData("интернет", "msedge.exe")]
    [InlineData("терминал", "cmd.exe")]
    [InlineData("консоль", "cmd.exe")]
    [InlineData("командная строка", "cmd.exe")]
    [InlineData("провера", "powershell.exe")]
    [InlineData("диспетчер задач", "taskmgr.exe")]
    [InlineData("настройки", "ms-settings:")]
    [InlineData("параметры", "ms-settings:")]
    public void Russian_names_resolve_to_the_right_executable(string spoken, string expected)
    {
        Assert.Equal(expected, AppAliases.Resolve(spoken));
    }

    [Theory]
    [InlineData("calculator", "calc.exe")]
    [InlineData("notepad", "notepad.exe")]
    [InlineData("paint", "mspaint.exe")]
    [InlineData("explorer", "explorer.exe")]
    [InlineData("browser", "msedge.exe")]
    [InlineData("cmd", "cmd.exe")]
    [InlineData("powershell", "powershell.exe")]
    [InlineData("task manager", "taskmgr.exe")]
    [InlineData("settings", "ms-settings:")]
    public void English_aliases_are_unchanged(string spoken, string expected)
    {
        Assert.Equal(expected, AppAliases.Resolve(spoken));
    }

    [Fact]
    public void Aliases_are_case_insensitive()
    {
        Assert.Equal("calc.exe", AppAliases.Resolve("КАЛЬКУЛЯТОР"));
        Assert.Equal("calc.exe", AppAliases.Resolve("Calculator"));
        Assert.Equal("calc.exe", AppAliases.Resolve("КалькуЛятоР"));
    }

    [Fact]
    public void Unknown_names_pass_through_so_real_executables_still_work()
    {
        Assert.Equal("C:\\Tools\\app.exe", AppAliases.Resolve("C:\\Tools\\app.exe"));
        Assert.Equal("my-program", AppAliases.Resolve("my-program"));
    }

    [Fact]
    public void Unknown_names_are_trimmed_but_not_rewritten()
    {
        Assert.Equal("steam.exe", AppAliases.Resolve("  steam.exe  "));
    }

    [Fact]
    public void Empty_and_null_never_become_a_launch_target()
    {
        Assert.Equal(string.Empty, AppAliases.Resolve(""));
        Assert.Equal(string.Empty, AppAliases.Resolve("   "));
        Assert.Equal(string.Empty, AppAliases.Resolve(null!));
    }

    [Fact]
    public void Every_russian_alias_contains_real_cyrillic_and_no_mojibake()
    {
        // Guards the exact defect this class was extracted for: an alias made of
        // CP1251 mojibake still "works" as a dictionary key but can never match speech.
        string[] spoken =
        [
            "калькулятор", "блокнот", "пейнт", "паинт", "рисовалка", "проводник",
            "файлы", "браузер", "интернет", "терминал", "консоль", "командная строка",
            "провера", "диспетчер задач", "настройки", "параметры",
        ];

        foreach (string name in spoken)
        {
            Assert.Matches("^[а-яё ]+$", name);
            // The tell-tale of the old corruption: capital Р/С mixed into lower-case text.
            Assert.DoesNotContain('Р', name);
            Assert.DoesNotContain('С', name);
            Assert.Equal(AppAliases.Resolve(name), AppAliases.Resolve(name.ToUpperInvariant()));
        }
    }

    [Fact]
    public void All_russian_aliases_map_to_a_known_windows_target()
    {
        foreach (string name in new[] { "калькулятор", "блокнот", "проводник", "терминал", "настройки" })
        {
            string resolved = AppAliases.Resolve(name);
            Assert.NotEqual(name, resolved);
            Assert.True(
                resolved.EndsWith(".exe", StringComparison.Ordinal) || resolved.StartsWith("ms-", StringComparison.Ordinal),
                $"{name} resolved to an unexpected target: {resolved}");
        }
    }
}
