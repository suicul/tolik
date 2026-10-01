namespace TOLIK.Contracts;

public sealed record ChatRequest(
    string? Message,
    bool CreateTask = false,
    Guid? ConversationId = null);

public sealed record ChatResponse(
    string Message,
    Guid? ConversationId,
    Guid? TaskId,
    string Source,
    string? Action = null,
    string? Details = null);
