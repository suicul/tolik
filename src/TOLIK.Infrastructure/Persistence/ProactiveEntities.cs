namespace TOLIK.Infrastructure.Persistence;

/// <summary>Durable reminder / proactive message (AGENTS.md §23). Survives Core restarts.</summary>
public sealed class TolikProactiveEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    /// <summary>When the message becomes eligible for delivery.</summary>
    public DateTimeOffset DueAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
