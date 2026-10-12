using System.Text.Json;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// CA-5 (docs/continuous-autonomy.md — PLAN REFORM PRG-3b): the absence/return
/// semantics. UserDisconnected ≠ TaskCancelled (the tasks live Core-side and the
/// supervisor watches them regardless of presence — architecturally guaranteed);
/// the RETURN is a real presence event: the mind's social drive rises and the owner
/// gets the proactive «what happened» summary (the journal — privacy by
/// construction: fixed kinds + generic summaries, safe for any authenticated reader).
/// </summary>
public sealed class PresenceReturnTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void User_returned_raises_the_social_drive_honestly()
    {
        var mind = new MindRuntime();
        var before = mind.Snapshot().SocialDrive;
        mind.OnUserReturned();
        var after = mind.Snapshot().SocialDrive;
        Assert.True(after > before); // a REAL presence event — the desire for interaction rises
        Assert.InRange(after, 0.0, 1.0);
    }

    [Fact]
    public void Presence_return_payload_carries_the_journal_summary()
    {
        var mind = new MindRuntime();
        mind.OnTaskStarted();
        mind.OnTaskCompleted();
        var json = TOLIK.Core.Host.CephalonExpressionService.BuildPresenceReturnJson(
            mind.Journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1)));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("presence_return", root.GetProperty("kind").GetString());
        var summary = root.GetProperty("summary").GetString();
        Assert.NotNull(summary);
        Assert.Contains("1 задача завершена", summary); // the real journal rendering flows
    }

    [Fact]
    public void Presence_return_is_private_by_construction()
    {
        // The summary renders ONLY the fixed kinds + the generic phrases — there is no
        // channel for user content to enter (the structural privacy invariant, §19/§50).
        var json = TOLIK.Core.Host.CephalonExpressionService.BuildPresenceReturnJson("За последний час: ничего существенного.");
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
    }
}
