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

/// <summary>The two mind-voice kinds the desktop lane understands. Both are «the
/// mind speaks» — one surface, one voice; the kind only selects a small cue.</summary>
public enum MessageKind
{
    /// <summary>MI-7: the mind noticed something (a failure streak) and tells the owner.</summary>
    Proactive,

    /// <summary>CA-5: the «what happened while you were away» digest, pushed on (re)connect.</summary>
    PresenceReturn,
}

/// <summary>
/// One parsed mind-voice message. Transport DTO: the Summary is Core's wording and
/// is presented VERBATIM — the display never rewrites, never truncates (an
/// over-long or broken summary is rejected whole upstream, never shown mangled).
/// At is the raw wire timestamp or null: like the expression frames, display
/// freshness is measured from local receipt (server clock skew must not matter).
/// </summary>
public sealed record ProactiveMessage(MessageKind Kind, string Summary, string? At);

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
///
/// The same class also parses the MIND-VOICE messages (TryParseMessage): the two
/// kinds the desktop lane speaks — <c>proactive</c> and <c>presence_return</c> —
/// share the envelope walk and the same fail-closed discipline: an unknown kind is
/// Ignored, a message claiming to be ours without a usable summary is Rejected
/// whole (never a mangled half-truth on the speech band).
/// </summary>
public static class ExpressionWireParser
{
    public const string WireKind = "cephalon_expression";
    public const string ProactiveWireKind = "proactive";
    public const string PresenceReturnWireKind = "presence_return";

    /// <summary>Display bound for a mind-voice summary. The live one-liners are
    /// ~80 chars; a larger payload is broken or hostile (§22: external content is
    /// untrusted) and is rejected WHOLE — truncating it into the band would be
    /// presenting a rewritten message, which the surface must never do.</summary>
    public const int MaxSummaryLength = 500;

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

            // Hub envelope form: {"type":"event","payload":{...}} (or the tolerated
            // bare-payload form) — shared with TryParseMessage below.
            if (!TryResolvePayload(root, out JsonElement payload))
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

    /// <summary>
    /// Parses a mind-voice message (proactive / presence_return) from the SAME
    /// socket stream the expression frames ride on. The two parsers coexist: each
    /// kind dispatches on <c>payload.kind</c>, so a frame is exactly one of
    /// {expression, message, other traffic}. Same outcome discipline as TryParse.
    /// </summary>
    public static ExpressionParseOutcome TryParseMessage(string? json, out ProactiveMessage? message)
    {
        message = null;
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

            if (!TryResolvePayload(root, out JsonElement payload))
            {
                return ExpressionParseOutcome.Ignored; // hello / unknown envelope shapes
            }

            if (!payload.TryGetProperty("kind", out JsonElement kind)
                || kind.ValueKind != JsonValueKind.String)
            {
                return ExpressionParseOutcome.Ignored; // the Android-lane duplicates carry type/text, not kind
            }

            MessageKind messageKind;
            switch (kind.GetString())
            {
                case ProactiveWireKind:
                    messageKind = MessageKind.Proactive;
                    break;
                case PresenceReturnWireKind:
                    messageKind = MessageKind.PresenceReturn;
                    break;
                default:
                    return ExpressionParseOutcome.Ignored; // unknown kind — safely not ours
            }

            if (!payload.TryGetProperty("summary", out JsonElement summary)
                || summary.ValueKind != JsonValueKind.String)
            {
                return ExpressionParseOutcome.Rejected; // ours, but the body is broken
            }

            string text = summary.GetString()!; // string-kind guarantees non-null
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSummaryLength)
            {
                return ExpressionParseOutcome.Rejected; // empty or oversized — never a mangled display
            }

            // "at" is optional display metadata: carried verbatim when Core sends a
            // string, dropped otherwise. It never gates acceptance — the summary is
            // the message; the timestamp is not worth silencing the mind over.
            string? at = null;
            if (payload.TryGetProperty("at", out JsonElement atElement)
                && atElement.ValueKind == JsonValueKind.String)
            {
                at = atElement.GetString();
            }

            message = new ProactiveMessage(messageKind, text, at);
            return ExpressionParseOutcome.Parsed;
        }
    }

    /// <summary>Resolves the hub envelope shared by both wire contracts:
    /// <c>{"type":"event","payload":{...}}</c> → the payload; a bare payload (the
    /// root itself carries "kind" — direct hub publish without envelope) → the root.
    /// False → not our shape at all (hello frames, alien envelopes) — Ignored, never
    /// Rejected: not-ours is normal traffic, not a broken contract.</summary>
    private static bool TryResolvePayload(JsonElement root, out JsonElement payload)
    {
        if (root.TryGetProperty("payload", out JsonElement envelopePayload))
        {
            if (root.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() != "event")
            {
                payload = default;
                return false;
            }
            if (envelopePayload.ValueKind != JsonValueKind.Object)
            {
                payload = default;
                return false;
            }
            payload = envelopePayload;
            return true;
        }

        if (root.TryGetProperty("kind", out _))
        {
            payload = root; // tolerated bare-payload form
            return true;
        }

        payload = default;
        return false;
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
