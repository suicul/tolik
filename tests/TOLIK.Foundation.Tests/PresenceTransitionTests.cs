using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

public sealed class PresenceTransitionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    [Fact]
    public void The_full_transition_flow_initiate_confirm_complete()
    {
        var engine = new PresenceTransitionEngine();
        var transition = engine.Initiate("pc-device", "phone-device", T0);
        Assert.NotNull(transition);
        Assert.Equal(PresenceTransitionState.Initiated, transition.State);
        var confirmed = engine.OldDeviceConfirm(transition.TransitionId, "pc-device", T0.AddSeconds(5));
        Assert.NotNull(confirmed);
        Assert.Equal(PresenceTransitionState.OldDeviceConfirmed, confirmed.State);
        var completed = engine.NewDeviceReady(transition.TransitionId, "phone-device", T0.AddSeconds(10));
        Assert.NotNull(completed);
        Assert.Equal(PresenceTransitionState.Completed, completed.State);
    }

    [Fact]
    public void Wrong_device_cannot_confirm_the_handoff()
    {
        var engine = new PresenceTransitionEngine();
        var transition = engine.Initiate("pc-device", "phone-device", T0);
        Assert.NotNull(transition);
        Assert.Null(engine.OldDeviceConfirm(transition.TransitionId, "phone-device", T0));
        Assert.Null(engine.NewDeviceReady(transition.TransitionId, "pc-device", T0));
    }

    [Fact]
    public void New_device_ready_requires_old_device_confirmation_first()
    {
        var engine = new PresenceTransitionEngine();
        var transition = engine.Initiate("pc-device", "phone-device", T0);
        Assert.NotNull(transition);
        Assert.Null(engine.NewDeviceReady(transition.TransitionId, "phone-device", T0));
    }

    [Fact]
    public void Cancel_aborts_the_transition()
    {
        var engine = new PresenceTransitionEngine();
        var transition = engine.Initiate("pc-device", "phone-device", T0);
        Assert.NotNull(transition);
        engine.OldDeviceConfirm(transition.TransitionId, "pc-device", T0);
        Assert.True(engine.Cancel(transition.TransitionId));
        Assert.Null(engine.OldDeviceConfirm(transition.TransitionId, "pc-device", T0.AddSeconds(1)));
    }

    [Fact]
    public void Only_one_transition_at_a_time()
    {
        var engine = new PresenceTransitionEngine();
        var first = engine.Initiate("pc-device", "phone-device", T0);
        Assert.NotNull(first);
        var second = engine.Initiate("phone-device", "tablet-device", T0.AddSeconds(5));
        Assert.Null(second);
    }
}
