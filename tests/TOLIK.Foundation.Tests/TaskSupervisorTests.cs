using TOLIK.Core;
using TOLIK.Core.Host;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// TaskSupervisorService (docs/continuous-autonomy.md CA-2 — PLAN REFORM PRG-3a):
/// the RUNNING-task watchdog — the liveness/stall detection with the bounded
/// escalation ladder (L1 the mind notices → L2 the owner notification → L3 the
/// pause: stop + preserve + ask). NEVER auto-cancel (the owner decides after the
/// pause); never an infinite loop (the levels capped); the activity resets the
/// counter; a restart never falsely stalls (the CreatedAt is the honest floor).
/// </summary>
public sealed class TaskSupervisorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(10);

    [Fact]
    public void Alive_task_gets_no_escalation()
    {
        var d = TaskSupervisorService.Decide(lastSeen: T0, now: T0.AddMinutes(5), Stall, stalledTicks: 0, pauseLevel: 3);
        Assert.Equal(0, d.Level);
        Assert.False(d.JournalMind || d.NotifyOwner || d.Pause);
    }

    [Fact]
    public void Level1_the_mind_notices_but_the_owner_is_not_spammed()
    {
        var d = TaskSupervisorService.Decide(T0, T0.AddMinutes(11), Stall, stalledTicks: 0, pauseLevel: 3);
        Assert.Equal(1, d.Level);
        Assert.True(d.JournalMind);
        Assert.False(d.NotifyOwner); // the first stall tick: TOLIK notices internally
        Assert.False(d.Pause);
    }

    [Fact]
    public void Level2_the_owner_is_notified()
    {
        var d = TaskSupervisorService.Decide(T0, T0.AddMinutes(21), Stall, stalledTicks: 1, pauseLevel: 3);
        Assert.Equal(2, d.Level);
        Assert.True(d.JournalMind);
        Assert.True(d.NotifyOwner);
        Assert.False(d.Pause);
    }

    [Fact]
    public void Level3_pauses_stop_preserve_ask_never_auto_cancel()
    {
        var d = TaskSupervisorService.Decide(T0, T0.AddMinutes(31), Stall, stalledTicks: 2, pauseLevel: 3);
        Assert.Equal(3, d.Level);
        Assert.True(d.JournalMind);
        Assert.True(d.NotifyOwner);
        Assert.True(d.Pause); // the existing lifecycle.Pause — Running → Paused (stop + preserve + ask)
    }

    [Fact]
    public void The_ladder_is_capped_no_escalation_beyond_pause()
    {
        var d = TaskSupervisorService.Decide(T0, T0.AddMinutes(61), Stall, stalledTicks: 9, pauseLevel: 3);
        Assert.True(d.Pause); // the pause stays the top — never a cancel, never an infinite growth
    }

    [Fact]
    public void Boundary_exactly_at_the_stall_limit_is_still_alive()
    {
        var d = TaskSupervisorService.Decide(T0, T0.Add(Stall), Stall, 0, 3); // exactly 10 min — not older
        Assert.Equal(0, d.Level);
    }

    [Fact]
    public void Config_parsing_fails_closed()
    {
        var oldTick = Environment.GetEnvironmentVariable(TaskSupervisorService.TickSecondsVariable);
        var oldStall = Environment.GetEnvironmentVariable(TaskSupervisorService.StallMinutesVariable);
        var oldPause = Environment.GetEnvironmentVariable(TaskSupervisorService.PauseLevelVariable);
        try
        {
            Environment.SetEnvironmentVariable(TaskSupervisorService.TickSecondsVariable, null);
            Environment.SetEnvironmentVariable(TaskSupervisorService.StallMinutesVariable, null);
            Environment.SetEnvironmentVariable(TaskSupervisorService.PauseLevelVariable, null);
            Assert.Equal(30.0, TaskSupervisorService.TickSeconds);
            Assert.Equal(10.0, TaskSupervisorService.StallMinutes);
            Assert.Equal(3, TaskSupervisorService.PauseLevel);
            Environment.SetEnvironmentVariable(TaskSupervisorService.TickSecondsVariable, "nonsense");
            Environment.SetEnvironmentVariable(TaskSupervisorService.StallMinutesVariable, "-5");
            Environment.SetEnvironmentVariable(TaskSupervisorService.PauseLevelVariable, "0");
            Assert.Equal(30.0, TaskSupervisorService.TickSeconds); // the invalid keeps the default
            Assert.Equal(10.0, TaskSupervisorService.StallMinutes);
            Assert.Equal(3, TaskSupervisorService.PauseLevel);
            Environment.SetEnvironmentVariable(TaskSupervisorService.TickSecondsVariable, "60");
            Environment.SetEnvironmentVariable(TaskSupervisorService.StallMinutesVariable, "15");
            Environment.SetEnvironmentVariable(TaskSupervisorService.PauseLevelVariable, "4");
            Assert.Equal(60.0, TaskSupervisorService.TickSeconds); // the valid parses
            Assert.Equal(15.0, TaskSupervisorService.StallMinutes);
            Assert.Equal(4, TaskSupervisorService.PauseLevel);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TaskSupervisorService.TickSecondsVariable, oldTick);
            Environment.SetEnvironmentVariable(TaskSupervisorService.StallMinutesVariable, oldStall);
            Environment.SetEnvironmentVariable(TaskSupervisorService.PauseLevelVariable, oldPause);
        }
    }

    [Fact]
    public void Mind_registers_the_stall_honestly()
    {
        var mind = new MindRuntime();
        mind.OnTaskStalled();
        var emotions = mind.ExpressionEmotions(T0);
        Assert.True(emotions.Uncertainty > 0);  // something is not progressing — a real signal
        var snapshot = mind.Snapshot();
        Assert.True(snapshot.Frustration > 0);  // mildly frustrating — honest
        var entries = mind.Journal.Recent(32);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.TaskStalled);
        Assert.Equal("Задача приостановилась", entries[^1].Summary);
    }

    [Fact]
    public void Stalled_tasks_render_in_the_journal_summary()
    {
        var journal = new MindJournal();
        journal.Record(MindJournalKind.TaskStalled, T0);
        var summary = journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("1 задача приостановилась", summary);
        journal.Record(MindJournalKind.TaskStalled, T0.AddSeconds(1));
        journal.Record(MindJournalKind.TaskStalled, T0.AddSeconds(2));
        var many = journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("3 задачи приостановлено", many); // the Russian plural holds
    }
}
