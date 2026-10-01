using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class ChatService
{
    public const int MaxMessageLength = 8192;

    public ChatResponse Answer(ChatRequest request, Guid? taskId = null, string? answer = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = Normalize(request.Message);
        var response = string.IsNullOrWhiteSpace(answer)
            ? $"TOLIK received your request: {message}"
            : Normalize(answer);

        return new ChatResponse(
            response,
            request.ConversationId ?? Guid.NewGuid(),
            taskId,
            string.IsNullOrWhiteSpace(answer) ? "core-fallback" : "model");
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty message is required.", nameof(value));

        var normalized = value.Trim();
        if (normalized.Length > MaxMessageLength)
            throw new ArgumentException($"Message cannot exceed {MaxMessageLength} characters.", nameof(value));

        return normalized;
    }
}
