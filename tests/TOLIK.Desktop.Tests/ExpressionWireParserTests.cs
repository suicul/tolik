using TOLIK.Contracts;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The wire parser is the trust boundary of the expression stream (CE-2): only a
/// FULLY parsed cephalon_expression frame may reach the renderer. Unknown kinds
/// and alien envelopes are safely ignored; anything that claims to be ours but
/// is broken is REJECTED — the last good state stands, nothing is half-rendered.
/// </summary>
public class ExpressionWireParserTests
{
    /// <summary>The EXACT live wire shape, verified against the running Core
    /// (heartbeat frame, field-for-field).</summary>
    private const string LiveIdleFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":{" +
        "\"eyeScale\":1.0,\"eyeOpenness\":0.75,\"eyeRotation\":0,\"asymmetry\":0,\"shellExpansion\":0," +
        "\"shellDeformation\":0,\"rotation\":0,\"pulseAmplitude\":0.08,\"glowIntensity\":0.25," +
        "\"particleDensity\":0.1,\"spectrumIntensity\":0,\"movementSpeed\":0.15,\"attentionAngle\":0," +
        "\"attentionIntensity\":0,\"transitionEnergy\":0.3,\"mode\":\"Idle\"}," +
        "\"at\":\"2026-10-11T12:00:00.0000000Z\"}}";

    [Fact]
    public void Live_contract_shape_parses_field_for_field()
    {
        ExpressionParseOutcome outcome = ExpressionWireParser.TryParse(LiveIdleFrame, out ExpressionFrame? frame);

        Assert.Equal(ExpressionParseOutcome.Parsed, outcome);
        Assert.NotNull(frame);
        Assert.Equal(1.0, frame!.EyeScale);
        Assert.Equal(0.75, frame.EyeOpenness);
        Assert.Equal(0.0, frame.EyeRotation);
        Assert.Equal(0.0, frame.Asymmetry);
        Assert.Equal(0.0, frame.ShellExpansion);
        Assert.Equal(0.0, frame.ShellDeformation);
        Assert.Equal(0.0, frame.Rotation);
        Assert.Equal(0.08, frame.PulseAmplitude);
        Assert.Equal(0.25, frame.GlowIntensity);
        Assert.Equal(0.1, frame.ParticleDensity);
        Assert.Equal(0.0, frame.SpectrumIntensity);
        Assert.Equal(0.15, frame.MovementSpeed);
        Assert.Equal(0.0, frame.AttentionAngle);
        Assert.Equal(0.0, frame.AttentionIntensity);
        Assert.Equal(0.3, frame.TransitionEnergy);
        Assert.Equal(ExpressionMode.Idle, frame.Mode);
    }

    [Fact]
    public void Full_activity_frame_parses_every_field_and_mode()
    {
        string json =
            "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":{" +
            "\"eyeScale\":1.3,\"eyeOpenness\":0.1,\"eyeRotation\":-0.25,\"asymmetry\":0.4," +
            "\"shellExpansion\":0.33,\"shellDeformation\":0.7,\"rotation\":0.42," +
            "\"pulseAmplitude\":0.9,\"glowIntensity\":1.0,\"particleDensity\":0.55," +
            "\"spectrumIntensity\":0.8,\"movementSpeed\":1.0,\"attentionAngle\":2.5," +
            "\"attentionIntensity\":0.6,\"transitionEnergy\":0.95,\"mode\":\"Speaking\"}," +
            "\"at\":\"2026-10-11T12:00:01Z\"}}";

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(1.3, frame!.EyeScale);
        Assert.Equal(0.1, frame.EyeOpenness);
        Assert.Equal(-0.25, frame.EyeRotation);
        Assert.Equal(0.33, frame.ShellExpansion);
        Assert.Equal(0.7, frame.ShellDeformation);
        Assert.Equal(0.42, frame.Rotation);
        Assert.Equal(0.9, frame.PulseAmplitude);
        Assert.Equal(1.0, frame.GlowIntensity);
        Assert.Equal(0.55, frame.ParticleDensity);
        Assert.Equal(0.8, frame.SpectrumIntensity);
        Assert.Equal(1.0, frame.MovementSpeed);
        Assert.Equal(2.5, frame.AttentionAngle);
        Assert.Equal(0.6, frame.AttentionIntensity);
        Assert.Equal(0.95, frame.TransitionEnergy);
        Assert.Equal(ExpressionMode.Speaking, frame.Mode);
    }

    [Fact]
    public void Unknown_kind_is_ignored_not_rejected()
    {
        // The hub carries other traffic (tasks, agents, proactive) — normal, not ours.
        string json =
            "{\"type\":\"event\",\"payload\":{\"kind\":\"task_started\",\"taskId\":\"42\",\"at\":\"x\"}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Non_event_envelope_is_ignored()
    {
        string json =
            "{\"type\":\"request\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":{}}}";

        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Malformed_json_is_rejected_and_yields_no_frame()
    {
        string truncated = LiveIdleFrame[..^40];

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParse(truncated, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Ours_but_missing_a_field_is_rejected()
    {
        string json = LiveIdleFrame.Replace("\"glowIntensity\":0.25,", string.Empty);

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Theory]
    [InlineData("\"eyeScale\":\"big\"")]
    [InlineData("\"eyeScale\":null")]
    [InlineData("\"eyeScale\":true")]
    public void Ours_but_non_numeric_field_is_rejected(string poison)
    {
        string json = LiveIdleFrame.Replace("\"eyeScale\":1.0", poison);

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Ours_but_unknown_mode_is_rejected()
    {
        string json = LiveIdleFrame.Replace("\"mode\":\"Idle\"", "\"mode\":\"Party\"");

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Ours_but_broken_expression_body_is_rejected()
    {
        string json =
            "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":42}}";

        Assert.Equal(ExpressionParseOutcome.Rejected, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated()
    {
        // Forward compatibility: Core may ADD fields; the renderer must not break.
        string json = LiveIdleFrame.Replace("\"mode\":\"Idle\"", "\"mode\":\"Idle\",\"futureField\":123");

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(ExpressionMode.Idle, frame!.Mode);
    }

    [Fact]
    public void Bare_payload_form_is_tolerated()
    {
        // A direct hub publish without the envelope is still an honest expression.
        string json =
            "{\"kind\":\"cephalon_expression\",\"expression\":{" +
            "\"eyeScale\":1.0,\"eyeOpenness\":0.75,\"eyeRotation\":0,\"asymmetry\":0," +
            "\"shellExpansion\":0,\"shellDeformation\":0,\"rotation\":0," +
            "\"pulseAmplitude\":0.08,\"glowIntensity\":0.25,\"particleDensity\":0.1," +
            "\"spectrumIntensity\":0,\"movementSpeed\":0.15,\"attentionAngle\":0," +
            "\"attentionIntensity\":0,\"transitionEnergy\":0.3,\"mode\":\"Idle\"}}";

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(ExpressionMode.Idle, frame!.Mode);
        Assert.Equal(0.75, frame.EyeOpenness);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"hello\"")]          // a bare JSON string — not our shape
    [InlineData("[1,2,3]")]            // an array — not our shape
    [InlineData("{}")]                 // an empty object — hello/noise frames
    public void Non_expression_input_is_ignored(string? json)
    {
        Assert.Equal(ExpressionParseOutcome.Ignored, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Integer_literals_parse_as_numbers()
    {
        // The live hub serializes whole numbers without decimals ("eyeRotation":0).
        string json = LiveIdleFrame.Replace("\"eyeOpenness\":0.75", "\"eyeOpenness\":1");

        Assert.Equal(ExpressionParseOutcome.Parsed, ExpressionWireParser.TryParse(json, out ExpressionFrame? frame));
        Assert.Equal(1.0, frame!.EyeOpenness);
    }
}
