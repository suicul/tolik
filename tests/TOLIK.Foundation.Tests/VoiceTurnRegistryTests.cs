using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Barge-in registry (docs/voice-runtime-v2.md M5): in-flight voice turns per user.
/// Core owns the registry — a client can only cancel its OWN turn (SECURITY §7).
/// A new turn for the same user auto-cancels the previous one (barge-in).
/// </summary>
public sealed class VoiceTurnRegistryTests
{
    [Fact]
    public void Cancel_cancels_in_flight_turn_and_returns_true()
    {
        var registry = new VoiceTurnRegistry();
        var userId = Guid.NewGuid();
        var cts = registry.Register(userId);
        Assert.False(cts.IsCancellationRequested);
        Assert.True(registry.Cancel(userId));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_with_no_turn_in_flight_returns_false()
    {
        var registry = new VoiceTurnRegistry();
        Assert.False(registry.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void Complete_removes_turn_without_cancelling()
    {
        var registry = new VoiceTurnRegistry();
        var userId = Guid.NewGuid();
        var cts = registry.Register(userId);
        registry.Complete(userId);
        Assert.False(cts.IsCancellationRequested);
        // After Complete, Cancel finds no turn in flight.
        Assert.False(registry.Cancel(userId));
    }

    [Fact]
    public void Second_register_for_same_user_auto_cancels_previous_barge_in()
    {
        var registry = new VoiceTurnRegistry();
        var userId = Guid.NewGuid();
        var first = registry.Register(userId);
        var second = registry.Register(userId);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Different_users_are_independent()
    {
        var registry = new VoiceTurnRegistry();
        var ivan = Guid.NewGuid();
        var maria = Guid.NewGuid();
        var ivanTurn = registry.Register(ivan);
        var mariaTurn = registry.Register(maria);
        // Maria's barge-in must never touch Ivan's turn (SECURITY §7).
        Assert.True(registry.Cancel(maria));
        Assert.True(mariaTurn.IsCancellationRequested);
        Assert.False(ivanTurn.IsCancellationRequested);
        Assert.True(registry.Cancel(ivan));
        Assert.True(ivanTurn.IsCancellationRequested);
    }
}
