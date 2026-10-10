using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// One parsed <c>cephalon_expression</c> frame — the EXACT 16-field wire contract
/// (verified field-by-field against Core's CephalonExpressionService.BuildWireJson,
/// commit 4def5df). This is a transport DTO: the renderer never invents values for
/// fields Core did not send, and never renders a frame it could not fully parse.
/// </summary>
public sealed record ExpressionFrame(
    double EyeScale,
    double EyeOpenness,
    double EyeRotation,
    double Asymmetry,
    double ShellExpansion,
    double ShellDeformation,
    double Rotation,
    double PulseAmplitude,
    double GlowIntensity,
    double ParticleDensity,
    double SpectrumIntensity,
    double MovementSpeed,
    double AttentionAngle,
    double AttentionIntensity,
    double TransitionEnergy,
    ExpressionMode Mode);

/// <summary>Parse result: Parsed → frame is real; Ignored → not ours (other kinds,
/// hello frames — normal traffic); Rejected → ours but broken (fail closed, never
/// render a half-truth).</summary>
public enum ExpressionParseOutcome
{
    Parsed,
    Ignored,
    Rejected,
}

/// <summary>
/// Wire parser for the realtime envelope <c>{"type":"event","payload":{...}}</c>.
/// - Only <c>cephalon_expression</c> payloads are extracted; everything else is
///   Ignored (the hub also carries task/agent/proactive events).
/// - Rejected frames (malformed JSON, missing/non-numeric fields, unknown mode,
///   non-finite values) NEVER reach the renderer — the last good state stands and
///   the event age grows honestly in the debug overlay.
/// - Unknown extra fields are ignored (forward compatible with additive changes).
/// - The "at" timestamp is deliberately NOT parsed: freshness is measured from local
///   receipt time (server clock skew must never corrupt the "event age" display).
/// </summary>
public static class ExpressionWireParser
{
    public const string WireKind = "cephalon_expression";

    public static ExpressionParseOutcome TryParse(string? json, out ExpressionFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return ExpressionParseOutcome.Ignored;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ExpressionParseOutcome.Rejected; // malformed — ours or not, it is garbage
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ExpressionParseOutcome.Ignored;
            }

            // Hub envelope form: {"type":"event","payload":{...}}.
            JsonElement payload;
            if (root.TryGetProperty("payload", out JsonElement envelopePayload))
            {
                if (root.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() != "event")
                {
                    return ExpressionParseOutcome.Ignored;
                }
                if (envelopePayload.ValueKind != JsonValueKind.Object)
                {
                    return ExpressionParseOutcome.Ignored;
                }
                payload = envelopePayload;
            }
            else if (root.TryGetProperty("kind", out _))
            {
                // Tolerated bare-payload form (direct hub publish without envelope).
                payload = root;
            }
            else
            {
                return ExpressionParseOutcome.Ignored; // hello / unknown envelope shapes
            }

            if (!payload.TryGetProperty("kind", out JsonElement kind))
            {
                return ExpressionParseOutcome.Ignored;
            }
            if (kind.ValueKind != JsonValueKind.String || kind.GetString() != WireKind)
            {
                return ExpressionParseOutcome.Ignored; // unknown kind — safely not ours
            }

            if (!payload.TryGetProperty("expression", out JsonElement expression)
                || expression.ValueKind != JsonValueKind.Object)
            {
                return ExpressionParseOutcome.Rejected; // ours, but the body is broken
            }

            double eyeScale = RequireNumber(expression, "eyeScale", out var failure);
            if (failure is not null) return Reject(failure);
            double eyeOpenness = RequireNumber(expression, "eyeOpenness", out failure);
            if (failure is not null) return Reject(failure);
            double eyeRotation = RequireNumber(expression, "eyeRotation", out failure);
            if (failure is not null) return Reject(failure);
            double asymmetry = RequireNumber(expression, "asymmetry", out failure);
            if (failure is not null) return Reject(failure);
            double shellExpansion = RequireNumber(expression, "shellExpansion", out failure);
            if (failure is not null) return Reject(failure);
            double shellDeformation = RequireNumber(expression, "shellDeformation", out failure);
            if (failure is not null) return Reject(failure);
            double rotation = RequireNumber(expression, "rotation", out failure);
            if (failure is not null) return Reject(failure);
            double pulseAmplitude = RequireNumber(expression, "pulseAmplitude", out failure);
            if (failure is not null) return Reject(failure);
            double glowIntensity = RequireNumber(expression, "glowIntensity", out failure);
            if (failure is not null) return Reject(failure);
            double particleDensity = RequireNumber(expression, "particleDensity", out failure);
            if (failure is not null) return Reject(failure);
            double spectrumIntensity = RequireNumber(expression, "spectrumIntensity", out failure);
            if (failure is not null) return Reject(failure);
            double movementSpeed = RequireNumber(expression, "movementSpeed", out failure);
            if (failure is not null) return Reject(failure);
            double attentionAngle = RequireNumber(expression, "attentionAngle", out failure);
            if (failure is not null) return Reject(failure);
            double attentionIntensity = RequireNumber(expression, "attentionIntensity", out failure);
            if (failure is not null) return Reject(failure);
            double transitionEnergy = RequireNumber(expression, "transitionEnergy", out failure);
            if (failure is not null) return Reject(failure);

            if (!expression.TryGetProperty("mode", out JsonElement modeElement)
                || modeElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse(modeElement.GetString(), ignoreCase: true, out ExpressionMode mode))
            {
                return Reject("unknown mode"); // contract evolved — fail closed, keep last good state
            }

            frame = new ExpressionFrame(
                eyeScale, eyeOpenness, eyeRotation, asymmetry, shellExpansion, shellDeformation,
                rotation, pulseAmplitude, glowIntensity, particleDensity, spectrumIntensity,
                movementSpeed, attentionAngle, attentionIntensity, transitionEnergy, mode);
            return ExpressionParseOutcome.Parsed;
        }
    }

    private static double RequireNumber(JsonElement expression, string name, out string? failure)
    {
        failure = null;
        if (!expression.TryGetProperty(name, out JsonElement value))
        {
            failure = $"missing {name}";
            return 0.0;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
        {
            failure = $"non-numeric {name}";
            return 0.0;
        }
        if (!double.IsFinite(number))
        {
            failure = $"non-finite {name}";
            return 0.0;
        }
        return number;
    }

    private static ExpressionParseOutcome Reject(string reason)
    {
        // The reason is intentionally not threaded to callers: the renderer's debug
        // shows the honest aggregate ("rejected frame — last good state stands").
        _ = reason;
        return ExpressionParseOutcome.Rejected;
    }
}
