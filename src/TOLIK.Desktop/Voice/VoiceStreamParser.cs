using System.Text.Json;

namespace TOLIK.Desktop.Voice;

/// <summary>One event from Core's voice stream (<c>POST /voice/turn-stream</c>).</summary>
public abstract record VoiceStreamEvent
{
    /// <summary>A text fragment of the answer, as the model produced it.</summary>
    public sealed record Delta(string Text) : VoiceStreamEvent;

    /// <summary>A playable audio chunk (the spoken part of the answer).</summary>
    public sealed record Audio(byte[] Data) : VoiceStreamEvent;

    /// <summary>Core reported a failure on an otherwise successful stream.</summary>
    public sealed record Error(string Reason) : VoiceStreamEvent;

    /// <summary>The turn ended normally.</summary>
    public sealed record Done : VoiceStreamEvent;
}

/// <summary>
/// Parses the voice-turn SSE stream into events.
///
/// The wire format is owned by Core (src/TOLIK.Core.Host/Program.cs, the
/// <c>/voice/turn-stream</c> handler): lines of <c>data: {json}</c> separated by a
/// blank line, with <c>type</c> in delta/audio/error/done. Unknown types and
/// comment/keep-alive lines are skipped rather than treated as failures — a future
/// Core event must not break an older client, and a broken client must never
/// fabricate an answer.
///
/// This class does no I/O: it turns one line into zero or one event, so the rules
/// are testable without a server or a socket.
/// </summary>
public static class VoiceStreamParser
{
    private const string DataPrefix = "data:";

    /// <summary>
    /// Interprets one SSE line. Returns null for blank lines, comments, keep-alives
    /// and unknown event types. Malformed audio is reported as an error event, never
    /// silently dropped: a stream that promised speech and produced garbage is a
    /// failure the user must be able to see.
    /// </summary>
    public static VoiceStreamEvent? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string trimmed = line.Trim();
        if (trimmed.StartsWith(':'))
        {
            // SSE comment / keep-alive.
            return null;
        }
        if (!trimmed.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string json = trimmed[DataPrefix.Length..].Trim();
        if (json.Length == 0)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new VoiceStreamEvent.Error("неразборчивый поток от Core");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return new VoiceStreamEvent.Error("поток без типа события");
            }

            switch (typeElement.GetString())
            {
                case "delta":
                    return root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                        ? new VoiceStreamEvent.Delta(text.GetString() ?? string.Empty)
                        : null;

                case "audio":
                    if (!root.TryGetProperty("audio", out JsonElement audio) || audio.ValueKind != JsonValueKind.String)
                    {
                        return new VoiceStreamEvent.Error("аудио без данных");
                    }
                    try
                    {
                        byte[] data = Convert.FromBase64String(audio.GetString() ?? string.Empty);
                        return data.Length == 0 ? null : new VoiceStreamEvent.Audio(data);
                    }
                    catch (FormatException)
                    {
                        return new VoiceStreamEvent.Error("повреждённый аудиопоток");
                    }

                case "error":
                    string reason = root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String
                        ? error.GetString() ?? "unknown"
                        : "unknown";
                    return new VoiceStreamEvent.Error(reason);

                case "done":
                    return new VoiceStreamEvent.Done();

                default:
                    // A newer Core may add events; an older client ignores what it
                    // does not understand instead of failing the whole turn.
                    return null;
            }
        }
    }
}
