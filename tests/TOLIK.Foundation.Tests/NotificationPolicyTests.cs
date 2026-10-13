using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// NotificationPolicy + Gate (docs/governance.md §5 Phase E2 — PLAN REFORM PRG-4b):
/// the proactive notifications are POLICIED. The fixed whitelist (the task results,
/// the reminders, the self-healing results, the statuses); the per-kind priorities;
/// the claimed self-configuration changes are NEVER notifications («я решил изменить
/// конфигурацию» без разрешения — that is a permission request, routed through the
/// confirmation flow, or a violation to report); the unknown kinds fail closed.
/// The rate limiting: the per-category + the global windows; the drops are honest
/// (the notifications inform — nothing authoritative ever flows through here).
/// </summary>
public sealed class NotificationPolicyTests
{
    [Fact]
    public void Informative_kinds_are_allowed_with_priorities()
    {
        var reminder = NotificationPolicy.Evaluate(NotificationKind.Reminder);
        Assert.True(reminder.Allowed);
        Assert.Equal(NotificationPriority.High, reminder.Priority);
        Assert.Equal(NotificationPriority.Normal, NotificationPolicy.Evaluate(NotificationKind.TaskCompleted).Priority);
        Assert.Equal(NotificationPriority.High, NotificationPolicy.Evaluate(NotificationKind.TaskFailed).Priority);
        Assert.Equal(NotificationPriority.Low, NotificationPolicy.Evaluate(NotificationKind.Status).Priority);
    }

    [Fact]
    public void Claimed_configuration_change_is_never_a_notification()
    {
        var verdict = NotificationPolicy.Evaluate(NotificationKind.ConfigurationChange);
        Assert.False(verdict.Allowed);
        Assert.NotNull(verdict.Reason);
        Assert.Contains("permission request", verdict.Reason); // the confirmation flow, never a plain notification
    }

    [Fact]
    public void Unknown_kinds_fail_closed()
    {
        Assert.False(NotificationPolicy.Evaluate("Событие от владельца").Allowed);
        Assert.False(NotificationPolicy.Evaluate(null).Allowed);
        Assert.False(NotificationPolicy.Evaluate("").Allowed);
    }
}

public sealed class NotificationGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Category_rate_limit_drops_the_burst_honestly()
    {
        var gate = new NotificationGate();
        for (var i = 0; i < NotificationGate.CategoryPerMinute; i++)
            Assert.True(gate.TrySend(NotificationKind.TaskStalled, T0.AddSeconds(i)).Allowed);
        var burst = gate.TrySend(NotificationKind.TaskStalled, T0.AddSeconds(30));
        Assert.False(burst.Allowed);                            // the burst is capped
        Assert.NotNull(burst.Reason);
        Assert.Contains("rate limit", burst.Reason);
    }

    [Fact]
    public void The_window_expires_and_different_categories_do_not_block_each_other()
    {
        var gate = new NotificationGate();
        for (var i = 0; i < NotificationGate.CategoryPerMinute; i++)
            gate.TrySend(NotificationKind.TaskStalled, T0.AddSeconds(i));
        Assert.False(gate.TrySend(NotificationKind.TaskStalled, T0.AddSeconds(30)).Allowed);
        // A different category is untouched by the task burst.
        Assert.True(gate.TrySend(NotificationKind.Reminder, T0.AddSeconds(31)).Allowed);
        // A minute later the task window has expired.
        Assert.True(gate.TrySend(NotificationKind.TaskStalled, T0.AddSeconds(70)).Allowed);
    }

    [Fact]
    public void Global_rate_limit_spans_categories()
    {
        var gate = new NotificationGate();
        var sent = 0;
        var now = T0;
        for (var i = 0; i < NotificationGate.GlobalPerMinute + 5; i++)
        {
            // Cycle the categories so no single category limit trips first.
            var kind = i % 3 == 0 ? NotificationKind.Status :
                       i % 3 == 1 ? NotificationKind.Reminder :
                                   NotificationKind.SelfHealingResult;
            if (gate.TrySend(kind, now).Allowed) sent++;
            now = now.AddSeconds(1);
        }
        Assert.Equal(NotificationGate.GlobalPerMinute, sent); // exactly the global cap reached
    }

    [Fact]
    public void Rejected_kinds_never_consume_rate_budget()
    {
        var gate = new NotificationGate();
        for (var i = 0; i < 20; i++)
            Assert.False(gate.TrySend(NotificationKind.ConfigurationChange, T0.AddSeconds(i)).Allowed); // the forbidden kind
        Assert.True(gate.TrySend(NotificationKind.Reminder, T0.AddSeconds(21)).Allowed); // the budget untouched by the rejections
    }
}
