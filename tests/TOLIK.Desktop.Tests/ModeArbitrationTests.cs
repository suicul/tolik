using TOLIK.Desktop.Presence;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Desktop Awareness (CE-4 — PLAN REFORM PRG-2b, директива §12): the auto mode
/// arbitration. THE OWNER'S MANUAL CHOICE IS SACRED — the automation only ever
/// touches a mode it set itself. The auto rules: a fullscreen GAME → Gaming (quiet,
/// click-through); another fullscreen app → Presentation; no fullscreen → back to
/// Normal. Emergency/Hidden/Focus are never auto-entered and never auto-left.
/// The game list: TOLIK_GAME_PROCESSES (the M8 pattern — the comma-separated,
/// case-insensitive, .exe-optional) — pure, unit-tested.
/// </summary>
public sealed class ModeArbitrationTests
{
    [Fact]
    public void Manual_choice_is_sacred_the_auto_never_overrides()
    {
        // The owner picked Gaming from the tray: a fullscreen non-game starts — the auto does NOT yank it.
        Assert.Null(ModeArbitration.Decide(PresenceMode.Gaming, currentIsAuto: false, fullscreen: true, gameProcess: false));
        // The owner picked Normal: a fullscreen game starts — the owner sees it in Normal (their choice).
        Assert.Null(ModeArbitration.Decide(PresenceMode.Normal, currentIsAuto: false, fullscreen: true, gameProcess: true));
        // The owner picked Hidden: nothing auto-changes it.
        Assert.Null(ModeArbitration.Decide(PresenceMode.Hidden, currentIsAuto: false, fullscreen: true, gameProcess: true));
    }

    [Fact]
    public void Auto_mode_switches_to_gaming_on_a_fullscreen_game()
    {
        Assert.Equal(PresenceMode.Gaming, ModeArbitration.Decide(PresenceMode.Normal, currentIsAuto: true, fullscreen: true, gameProcess: true));
        // Already there → no change (null).
        Assert.Null(ModeArbitration.Decide(PresenceMode.Gaming, currentIsAuto: true, fullscreen: true, gameProcess: true));
    }

    [Fact]
    public void Auto_mode_switches_to_presentation_on_other_fullscreen_apps()
    {
        Assert.Equal(PresenceMode.Presentation, ModeArbitration.Decide(PresenceMode.Normal, currentIsAuto: true, fullscreen: true, gameProcess: false));
        Assert.Null(ModeArbitration.Decide(PresenceMode.Presentation, currentIsAuto: true, fullscreen: true, gameProcess: false));
    }

    [Fact]
    public void Auto_mode_returns_to_normal_when_the_fullscreen_ends()
    {
        Assert.Equal(PresenceMode.Normal, ModeArbitration.Decide(PresenceMode.Gaming, currentIsAuto: true, fullscreen: false, gameProcess: false));
        Assert.Equal(PresenceMode.Normal, ModeArbitration.Decide(PresenceMode.Presentation, currentIsAuto: true, fullscreen: false, gameProcess: false));
        // Already Normal — no change.
        Assert.Null(ModeArbitration.Decide(PresenceMode.Normal, currentIsAuto: true, fullscreen: false, gameProcess: false));
    }

    [Fact]
    public void Emergency_hidden_and_focus_are_never_auto_touched()
    {
        // These modes only respond to the owner — the fullscreen state never enters or leaves them.
        Assert.Null(ModeArbitration.Decide(PresenceMode.Emergency, currentIsAuto: true, fullscreen: true, gameProcess: true));
        Assert.Null(ModeArbitration.Decide(PresenceMode.Hidden, currentIsAuto: true, fullscreen: true, gameProcess: true));
        Assert.Null(ModeArbitration.Decide(PresenceMode.Focus, currentIsAuto: true, fullscreen: false, gameProcess: false));
    }
}

public sealed class GameProcessMatcherTests
{
    [Theory]
    [InlineData("cyberpunk2077", "Cyberpunk2077.exe", true)]   // case-insensitive, .exe tolerated
    [InlineData("Cyberpunk2077", "cyberpunk2077", true)]       // exact
    [InlineData("explorer", "Cyberpunk2077.exe, explorer", true)] // the list match
    [InlineData("EXPLORER", "explorer", true)]                  // the process case varies
    [InlineData("notepad", "Cyberpunk2077.exe; explorer", false)] // separators ; and ,
    [InlineData("", "explorer", false)]                          // no process — false
    [InlineData("explorer", "", false)]                           // no list — false
    [InlineData("explorer", "   ", false)]                        // blank list — false
    [InlineData("explorer.exe", "explorer", true)]                // the observed name may carry .exe
    public void Game_matching_is_forgiving_and_case_insensitive(string process, string list, bool expected)
    {
        Assert.Equal(expected, DesktopWatcher.IsGameProcess(process, list));
    }

    [Fact]
    public void Observation_is_safe_by_construction()
    {
        // The observation struct carries ONLY the process name + the fullscreen flag —
        // no window titles, no content, nothing else exists in the type (§50: privacy
        // by construction — the API cannot leak what it never collects).
        var obs = new DesktopWatcher.DesktopObservation(Fullscreen: true, ProcessName: "game");
        Assert.True(obs.Fullscreen);
        Assert.Equal("game", obs.ProcessName);
    }
}
