namespace TOLIK.Infrastructure.Persistence;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid CorrelationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? DeviceId { get; set; }
    public string? SessionId { get; set; }
    public string? Capability { get; set; }
    public string? Risk { get; set; }
    public string? Resource { get; set; }
    public string? Result { get; set; }
    public string Severity { get; set; } = "SECURITY";
    public string? PreviousHash { get; set; }
    public string IntegrityHash { get; set; } = string.Empty;
}
