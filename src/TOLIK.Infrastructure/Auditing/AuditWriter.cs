using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Logging;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Auditing;

public sealed class AuditWriter(IDbContextFactory<TolikDbContext> contextFactory)
{
    public async Task WriteAsync(Guid correlationId, string eventType, string decision, string reason, CancellationToken cancellationToken = default)
        => await WriteAuthorizationAsync(correlationId, eventType, decision, reason, null, null, null, null, null, cancellationToken);

    public async Task WriteAuthorizationAsync(
        Guid correlationId,
        string eventType,
        string decision,
        string reason,
        SessionPrincipal? principal,
        AgentCapability? capability,
        ToolRiskLevel? risk,
        string? resource,
        string? result,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.audit-chain'))", cancellationToken);
        var previousHash = await db.AuditEvents
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.IntegrityHash)
            .FirstOrDefaultAsync(cancellationToken);
        var occurredAt = DateTimeOffset.UtcNow;
        var safeEventType = Limit(SecretRedactor.Redact(eventType), 128);
        var safeDecision = Limit(SecretRedactor.Redact(decision), 32);
        var safeReason = Limit(SecretRedactor.Redact(reason), 512);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OccurredAt = occurredAt, CorrelationId = correlationId,
            EventType = safeEventType, Decision = safeDecision, Reason = safeReason,
            UserId = principal?.UserId.ToString("D"), DeviceId = principal?.DeviceId.ToString("D"),
            SessionId = principal?.SessionId.ToString("D"), Capability = capability?.ToString(),
            Risk = risk?.ToString(), Resource = resource is null ? null : Limit(SecretRedactor.Redact(resource), 512),
            Result = result is null ? null : Limit(SecretRedactor.Redact(result), 128),
            PreviousHash = previousHash,
            IntegrityHash = ComputeIntegrityHash(correlationId, occurredAt, safeEventType, safeDecision, safeReason, previousHash)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static string ComputeIntegrityHash(Guid correlationId, DateTimeOffset occurredAt, string eventType, string decision, string reason, string? previousHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", previousHash ?? string.Empty, correlationId.ToString("D"), occurredAt.ToUniversalTime().ToString("O"), eventType, decision, reason))));

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
