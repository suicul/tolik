using System.Diagnostics;
using TOLIK.Contracts;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The presenter is the honesty contract of CE-2, verified with a fake clock:
/// the owner must ALWAYS be able to tell apart (1) no connection, (2) connected
/// but awaiting events, (3) auth rejected, (4) events flowing — and a stream that
/// goes silent must drop the drive instead of holding a dead pose.
/// </summary>
public class ExpressionLinkPresenterTests
{
    private sealed class FakeClock
    {
        public long Ticks { get; private set; }

        public long Timestamp() => Ticks;

        public void AdvanceSeconds(double seconds) =>
            Ticks += (long)(seconds * Stopwatch.Frequency);
    }

    private static ExpressionFrame IdleFrame { get; } = new(
        EyeScale: 1.0, EyeOpenness: 0.75, EyeRotation: 0, Asymmetry: 0,
        ShellExpansion: 0, ShellDeformation: 0, Rotation: 0,
        PulseAmplitude: 0.08, GlowIntensity: 0.25, ParticleDensity: 0.1,
        SpectrumIntensity: 0, MovementSpeed: 0.15, AttentionAngle: 0,
        AttentionIntensity: 0, TransitionEnergy: 0.3, Mode: ExpressionMode.Idle);

    private static (ExpressionLinkPresenter Presenter, FakeClock Clock) New(
        CephalonAnimator? animator = null)
    {
        var clock = new FakeClock();
        animator ??= new CephalonAnimator(IdleMotionConfig.Default, new XorShiftRandom(0x51AE));
        var presenter = new ExpressionLinkPresenter(animator, "127.0.0.1:5080", clock.Timestamp);
        return (presenter, clock);
    }

    // ---------- the four honest states ----------

    [Fact]
    public void State1_no_connection_shows_it_and_runs_procedural_idle()
    {
        (ExpressionLinkPresenter link, _) = New();

        Assert.Equal("no Core connection", link.CoreLine);
        Assert.Equal("procedural idle", link.ExpressionLine);
        Assert.Null(link.ReasonLine); // "starting" is not a reason worth showing
        Assert.False(link.DriveApplied);
        Assert.Null(link.LastMode);
    }

    [Fact]
    public void State2_connected_awaiting_events_stays_procedural_idle()
    {
        (ExpressionLinkPresenter link, _) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connecting, "127.0.0.1"));
        Assert.Equal("connecting — 127.0.0.1:5080", link.CoreLine);

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));

        Assert.Equal("connected — awaiting expression events", link.CoreLine);
        Assert.Equal("procedural idle", link.ExpressionLine); // no events yet — honest idle
        Assert.False(link.DriveApplied);
    }

    [Fact]
    public void State3_auth_rejected_is_worded_distinctly_from_offline()
    {
        (ExpressionLinkPresenter link, _) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.AuthRejected,
            "auth rejected (401/403) — Core denied a fresh dev token; not reconnecting (127.0.0.1)"));

        Assert.Equal("AUTH REJECTED — not reconnecting", link.CoreLine);
        Assert.Contains("auth rejected", link.ReasonLine);
        Assert.NotEqual("no Core connection", link.CoreLine); // never look alike
        Assert.Equal("procedural idle", link.ExpressionLine);
        Assert.False(link.DriveApplied);
    }

    [Fact]
    public void State4_events_flowing_show_connection_mode_and_age()
    {
        (ExpressionLinkPresenter link, FakeClock clock) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));
        link.OnExpressionFrame(IdleFrame);

        Assert.True(link.DriveApplied);
        Assert.Equal("Idle", link.LastMode);
        Assert.Equal("connected — 127.0.0.1:5080", link.CoreLine);
        Assert.Equal("Idle · 0.0s ago", link.ExpressionLine);

        clock.AdvanceSeconds(1.234);
        Assert.Equal(1.234, link.LastEventAgeSeconds!.Value, 2);
        Assert.Equal("Idle · 1.2s ago", link.ExpressionLine);
    }

    // ---------- fallbacks are never fabricated ----------

    [Fact]
    public void Disconnection_drops_the_drive_immediately_and_says_why()
    {
        (ExpressionLinkPresenter link, _) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));
        link.OnExpressionFrame(IdleFrame);
        Assert.True(link.DriveApplied);

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Disconnected, "connection lost — 127.0.0.1"));

        Assert.False(link.DriveApplied); // no pose held on dead strings
        Assert.Equal("no Core connection", link.CoreLine);
        Assert.Equal("connection lost — 127.0.0.1", link.ReasonLine);
        Assert.Equal("procedural idle", link.ExpressionLine); // not a stale "Idle · 40s ago"
    }

    [Fact]
    public void Reconnect_without_new_events_stays_procedural_idle()
    {
        (ExpressionLinkPresenter link, _) = New();

        link.OnExpressionFrame(IdleFrame);
        link.OnLinkState(new LinkSnapshot(CoreLinkState.Disconnected, "connection lost — 127.0.0.1"));
        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connecting, "127.0.0.1"));
        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));

        // Connected again but NO fresh event yet — the OLD event's age must not
        // be presented as a live expression.
        Assert.False(link.DriveApplied);
        Assert.Equal("connected — awaiting expression events", link.CoreLine);
        Assert.Equal("procedural idle", link.ExpressionLine);
    }

    [Fact]
    public void A_silent_stream_goes_stale_and_never_holds_a_frozen_pose()
    {
        (ExpressionLinkPresenter link, FakeClock clock) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));
        link.OnExpressionFrame(IdleFrame);

        clock.AdvanceSeconds(ExpressionLinkPresenter.StaleAfterSeconds - 0.5);
        link.Tick();
        Assert.True(link.DriveApplied); // still within the heartbeat budget

        clock.AdvanceSeconds(1.0); // past the deadline
        link.Tick();

        Assert.False(link.DriveApplied);
        Assert.StartsWith("stale — local idle", link.ExpressionLine);
        Assert.Contains("Idle", link.ExpressionLine);
        Assert.Equal("connected — 127.0.0.1:5080", link.CoreLine); // the LINK is fine; the STREAM died
    }

    [Fact]
    public void A_fresh_frame_revives_a_stale_stream()
    {
        (ExpressionLinkPresenter link, FakeClock clock) = New();

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));
        link.OnExpressionFrame(IdleFrame);
        clock.AdvanceSeconds(ExpressionLinkPresenter.StaleAfterSeconds + 1);
        link.Tick();
        Assert.False(link.DriveApplied);

        link.OnExpressionFrame(IdleFrame with { Mode = ExpressionMode.Thinking });

        Assert.True(link.DriveApplied);
        Assert.Equal("Thinking", link.LastMode);
        Assert.Equal("Thinking · 0.0s ago", link.ExpressionLine);
    }

    // ---------- debug text hygiene ----------

    [Fact]
    public void Long_reasons_are_capped_for_the_overlay()
    {
        (ExpressionLinkPresenter link, _) = New();

        string longDetail = new string('x', 100);
        link.OnLinkState(new LinkSnapshot(CoreLinkState.Disconnected, longDetail));

        Assert.NotNull(link.ReasonLine);
        Assert.True(link.ReasonLine!.Length <= 52, "reason must fit the overlay");
        Assert.EndsWith("…", link.ReasonLine);
    }

    [Fact]
    public void Frames_apply_the_drive_through_the_bridge()
    {
        CephalonAnimator animator =
            new(IdleMotionConfig.Default, new XorShiftRandom(0x51AE));
        var clock = new FakeClock();
        var link = new ExpressionLinkPresenter(animator, "core", clock.Timestamp);

        link.OnLinkState(new LinkSnapshot(CoreLinkState.Connected, "connected — awaiting expression events"));
        link.OnExpressionFrame(IdleFrame with { Mode = ExpressionMode.Speaking, EyeOpenness = 0.9 });

        Assert.True(animator.HasDrive);
        Assert.Equal(CephalonState.Speaking, animator.State);
        Assert.Equal(CephalonTone.Voice, animator.Traits.Tone);
    }

    [Fact]
    public void Null_inputs_are_rejected()
    {
        (ExpressionLinkPresenter link, _) = New();
        Assert.Throws<ArgumentNullException>(() => link.OnLinkState(null!));
        Assert.Throws<ArgumentNullException>(() => link.OnExpressionFrame(null!));
    }
}
