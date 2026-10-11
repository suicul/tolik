using System.Text.Json;
using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Core.Host;
using Xunit;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Cephalon Expression State Watcher (docs/cephalon-desktop.md CE-2) — the pure core:
/// honest state mapping (§50: real sources or honest zeros, Mind never wired), per-user
/// isolation (privacy §19/§50), publish gating (no identical-frame spam), fail-closed
/// config parsing (§41) and the FIXED wire contract (the desktop/Android lanes consume
/// exactly this shape).
/// </summary>
public sealed class CephalonExpressionServiceTests
{
    private static CephalonUserSnapshot WorkingSnapshot(double progress = 0.4) => new(RunningTasks: 2, TaskProgress: progress);
    private static readonly CephalonUserSnapshot EmptySnapshot = CephalonUserSnapshot.Empty;

    /// <summary>Nearly-converged compose (dt=1 with the composer's internal clamp).
    /// MindEmotions default to the zero record — the pure-mapping tests pass explicit values.</summary>
    private static readonly MindEmotions MindZero = new(Curiosity: 0, Frustration: 0, Confidence: 0, Attention: 0);

    private static ExpressionOutput ComposeConverged(
        CephalonUserSnapshot snapshot, bool locked = false, bool turnInFlight = false, bool connected = true,
        ExpressionOutput? previous = null, MindEmotions? emotions = null) =>
        CephalonExpressionService.ComposeFor(snapshot, locked, turnInFlight, connected, emotions ?? MindZero, previous, deltaSeconds: 1.0);

    // ─────────────────────── Mind wired (PLAN REFORM PRG-1a) ───────────────────────

    [Fact]
    public void Mind_drives_flow_while_unwired_sources_stay_honest_zeros()
    {
        // PRG-1a (12.10.2026): the mind IS wired — the real drives FLOW into the
        // expression; sources that do not exist yet stay honest zeros (§50).
        var emotions = new MindEmotions(Curiosity: 0.7, Frustration: 0.2, Confidence: 0.5, Attention: 0.8);
        var state = CephalonExpressionService.ToExpressionState(WorkingSnapshot(progress: 0.6), locked: false, turnInFlight: false, connected: true, emotions);
        // Mind drives flow (wired):
        Assert.Equal(0.7, state.Curiosity);
        Assert.Equal(0.2, state.Frustration);
        Assert.Equal(0.5, state.Confidence);
        Assert.Equal(0.8, state.Attention);
        // No real source yet — honest zeros, never fabricated:
        Assert.Equal(0, state.Amusement);
        Assert.Equal(0, state.Uncertainty);
        Assert.Equal(0, state.Thinking);
        Assert.Equal(0, state.Recovery);
        Assert.Equal(SystemSignalLevel.None, state.SignalLevel);
        // Voice bands: no audio telemetry in Core (CE-3 remainder) — honest zeros.
        Assert.Equal(0, state.Amplitude);
        Assert.Equal(0, state.LowBand);
        Assert.Equal(0, state.MidBand);
        Assert.Equal(0, state.HighBand);
        // Real sources ARE wired: tasks + presence.
        Assert.Equal(1.0, state.Processing);      // running tasks exist
        Assert.Equal(0.6, state.TaskProgress);    // lifecycle evidence snapshot
        Assert.Equal(1.0, state.Engagement);      // live authenticated connection
        Assert.False(state.Locked);
        Assert.False(state.IsSpeaking);
    }

    [Fact]
    public void Empty_snapshot_composes_to_honest_idle()
    {
        var state = CephalonExpressionService.ToExpressionState(EmptySnapshot, locked: false, turnInFlight: false, connected: false, MindZero);
        Assert.Equal(0, state.Processing);
        Assert.Equal(0, state.TaskProgress);
        Assert.Equal(0, state.Engagement);
        Assert.Equal(ExpressionMode.Idle, ComposeConverged(EmptySnapshot, connected: false).Mode);
    }

    [Fact]
    public void Running_task_composes_to_working_mode()
    {
        Assert.Equal(ExpressionMode.Working, ComposeConverged(WorkingSnapshot()).Mode);
    }

    [Fact]
    public void Voice_turn_wires_isSpeaking_with_honest_zero_bands()
    {
        // The turn lifecycle is the coarse isSpeaking source; the spectrum stays zero
        // (CE-3 remainder) — Speaking mode with a calm body, never a fake waveform.
        var state = CephalonExpressionService.ToExpressionState(WorkingSnapshot(), locked: false, turnInFlight: true, connected: true, MindZero);
        Assert.True(state.IsSpeaking);
        Assert.Equal(0, state.Amplitude);
        Assert.Equal(0, state.LowBand);
        Assert.Equal(0, state.MidBand);
        Assert.Equal(0, state.HighBand);
        // Voice overrides the mode hint (директива §9) — even over an active task.
        Assert.Equal(ExpressionMode.Speaking, ComposeConverged(WorkingSnapshot(), turnInFlight: true).Mode);
    }

    [Fact]
    public void Locked_fails_closed_even_with_tasks_and_voice()
    {
        var output = ComposeConverged(WorkingSnapshot(), locked: true, turnInFlight: true);
        Assert.Equal(ExpressionMode.Locked, output.Mode); // absolute priority (§10)
        Assert.True(output.IsFinite);
    }

    [Fact]
    public void Nan_progress_never_renders_a_spike()
    {
        // §18 fail-closed: garbage progress input collapses to neutral — the composer
        // normalizes (NaN → 0); the output stays finite and bounded.
        var output = ComposeConverged(WorkingSnapshot(progress: double.NaN));
        Assert.True(output.IsFinite);
        Assert.InRange(output.PulseAmplitude, 0.0, 1.0);
        Assert.Equal(ExpressionMode.Working, output.Mode);
    }

    // ─────────────────────────── Per-user isolation ───────────────────────────

    [Fact]
    public void User_states_are_composed_only_from_their_own_snapshot()
    {
        // Privacy §19/§50: user A's tasks never leak into user B's expression — the
        // composition reads ONLY the snapshot passed for that user.
        var userA = ComposeConverged(WorkingSnapshot(progress: 0.8));
        var userB = ComposeConverged(EmptySnapshot);
        Assert.Equal(ExpressionMode.Working, userA.Mode);
        Assert.Equal(ExpressionMode.Idle, userB.Mode);

        // Cross-contamination check: composing B again (in any order, with B's own
        // previous) yields the identical idle result — the pure function has no shared
        // state to leak through.
        var userBAfterA = ComposeConverged(EmptySnapshot, previous: ComposeConverged(EmptySnapshot));
        Assert.Equal(ExpressionMode.Idle, userBAfterA.Mode);
    }

    // ─────────────────────────── Publish gating ───────────────────────────

    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);

    [Fact]
    public void First_frame_is_always_published()
    {
        Assert.True(CephalonExpressionService.ShouldPublish(null, ExpressionOutput.Neutral, TimeSpan.Zero, Heartbeat));
    }

    [Fact]
    public void Identical_frames_are_not_published()
    {
        var output = ExpressionOutput.Neutral with { Mode = ExpressionMode.Idle };
        Assert.False(CephalonExpressionService.ShouldPublish(output, output with { }, TimeSpan.FromMilliseconds(100), Heartbeat));
    }

    [Fact]
    public void Mode_change_is_always_published()
    {
        Assert.True(CephalonExpressionService.ShouldPublish(
            ExpressionOutput.Neutral, ExpressionOutput.Neutral with { Mode = ExpressionMode.Working },
            TimeSpan.FromMilliseconds(100), Heartbeat));
    }

    [Fact]
    public void Small_drift_is_not_published_but_meaningful_movement_is()
    {
        var previous = ExpressionOutput.Neutral;
        // 0.005 — below the threshold: silent.
        var tiny = previous with { GlowIntensity = previous.GlowIntensity + 0.005 };
        Assert.False(CephalonExpressionService.ShouldPublish(previous, tiny, TimeSpan.FromMilliseconds(100), Heartbeat));
        // 0.02 — a meaningful move: published immediately.
        var moved = previous with { GlowIntensity = previous.GlowIntensity + 0.02 };
        Assert.True(CephalonExpressionService.ShouldPublish(previous, moved, TimeSpan.FromMilliseconds(100), Heartbeat));
    }

    [Fact]
    public void Heartbeat_elapsed_is_published_even_when_nothing_moved()
    {
        Assert.True(CephalonExpressionService.ShouldPublish(
            ExpressionOutput.Neutral, ExpressionOutput.Neutral, TimeSpan.FromSeconds(1.1), Heartbeat));
    }

    [Fact]
    public void Non_finite_candidate_never_broadcasts()
    {
        var corrupted = new ExpressionOutput(double.NaN, 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.False(corrupted.IsFinite);
        Assert.False(CephalonExpressionService.ShouldPublish(null, corrupted, TimeSpan.Zero, Heartbeat));
    }

    // ─────────────────────────── Wire contract (EXACT) ───────────────────────────

    [Fact]
    public void Wire_json_matches_the_fixed_contract_exactly()
    {
        var output = ComposeConverged(WorkingSnapshot()); // → Working
        var json = CephalonExpressionService.BuildWireJson(output, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Root: exactly {kind, expression, at}.
        var rootNames = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "kind", "expression", "at" }, rootNames);
        Assert.Equal("cephalon_expression", root.GetProperty("kind").GetString());

        // Expression: exactly the 16 camelCase contract fields.
        var expression = root.GetProperty("expression");
        var expected = new[]
        {
            "eyeScale", "eyeOpenness", "eyeRotation", "asymmetry", "shellExpansion",
            "shellDeformation", "rotation", "pulseAmplitude", "glowIntensity",
            "particleDensity", "spectrumIntensity", "movementSpeed", "attentionAngle",
            "attentionIntensity", "transitionEnergy", "mode"
        };
        var actual = expression.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(expected, actual);

        // Values: mode is the enum NAME string; numbers finite; at is ISO-8601.
        Assert.Equal("Working", expression.GetProperty("mode").GetString());
        foreach (var property in expression.EnumerateObject())
            if (property.Name != "mode")
                Assert.True(double.IsFinite(property.Value.GetDouble()), $"{property.Name} must be finite");
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("at").GetString(), out _));
    }

    [Fact]
    public void Wire_mode_names_match_the_contract_vocabulary()
    {
        foreach (var (mode, expected) in new[]
        {
            (ExpressionMode.Idle, "Idle"), (ExpressionMode.Thinking, "Thinking"),
            (ExpressionMode.Speaking, "Speaking"), (ExpressionMode.Working, "Working"),
            (ExpressionMode.Recovering, "Recovering"), (ExpressionMode.Alert, "Alert"),
            (ExpressionMode.Locked, "Locked")
        })
        {
            var json = CephalonExpressionService.BuildWireJson(ExpressionOutput.Neutral with { Mode = mode }, DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(expected, doc.RootElement.GetProperty("expression").GetProperty("mode").GetString());
        }
    }

    // ─────────────────────── Fail-closed configuration (§41) ───────────────────────

    [Theory]
    [InlineData(null, 200.0)]     // unset → default 5 Hz
    [InlineData("", 200.0)]
    [InlineData("abc", 200.0)]    // non-numeric → default
    [InlineData("NaN", 200.0)]    // non-finite → default
    [InlineData("-3", 200.0)]     // negative → default
    [InlineData("0", 200.0)]      // zero → default
    [InlineData("10", 100.0)]     // 10 Hz → 100 ms
    [InlineData("0.5", 2000.0)]   // 0.5 Hz → 2 s
    [InlineData("1000", 10.0)]    // degenerate high value → 10 ms CPU floor
    public void Broadcast_hz_fails_closed(string? raw, double expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, CephalonExpressionService.TickIntervalFromConfig(raw).TotalMilliseconds);
    }

    [Theory]
    [InlineData(null, 1.0)]      // unset → default 1 s
    [InlineData("abc", 1.0)]     // non-numeric → default
    [InlineData("-1", 1.0)]      // negative → default
    [InlineData("0", 1.0)]       // zero → default
    [InlineData("2.5", 2.5)]
    public void Heartbeat_fails_closed(string? raw, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, CephalonExpressionService.HeartbeatFromConfig(raw).TotalSeconds);
    }

    // ─────────────────────── Additive accessor sanity ───────────────────────

    [Fact]
    public void Hub_without_clients_reports_no_connected_users()
    {
        using var hub = new RealtimeEventHub();
        Assert.Empty(hub.ConnectedUserIds);
    }

    [Fact]
    public void Voice_turn_registry_probe_tracks_the_turn_lifecycle()
    {
        var registry = new VoiceTurnRegistry();
        var userId = Guid.NewGuid();
        Assert.False(registry.HasInFlightTurn(userId));
        var cts = registry.Register(userId);
        Assert.True(registry.HasInFlightTurn(userId)); // isSpeaking source is live
        registry.Complete(userId);
        Assert.False(registry.HasInFlightTurn(userId));
        cts.Dispose();
    }
}
