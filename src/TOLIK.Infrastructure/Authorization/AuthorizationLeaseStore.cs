using System.Collections.Concurrent;
using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Ported from interface/identity.js issueLease/validateLease/revokeLease (stage 2c of
/// Option A, docs/INTERFACE-ANALYSIS.md): a lease is a short-lived per-run authorization
/// with a TTL (60s default) and an explicit revocation. Unknown, revoked or expired
/// leases fail closed — they authorize nothing. The lease ceiling is the maximum risk
/// band the run can ever reach; the offline cap (read-only band) is enforced on the
/// effective risk by <see cref="AuthorizationPolicy"/> via the OfflineMode flag.
/// </summary>
public sealed class AuthorizationLease
{
    private readonly object _consumedGate = new();
    private readonly HashSet<string> _consumedAuthorizations = new(StringComparer.Ordinal);

    public AuthorizationLease(Guid id, Guid sessionId, ToolRiskLevel riskCeiling, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        Id = id;
        SessionId = sessionId;
        RiskCeiling = riskCeiling;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }
    public Guid SessionId { get; }
    public ToolRiskLevel RiskCeiling { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool Revoked { get; internal set; }
    public string? RevokedReason { get; internal set; }

    /// <summary>Single-use consumption of one authorization inside the lease (prototype consumeAuthorization).</summary>
    public bool ConsumeAuthorization(string authorizationId)
    {
        lock (_consumedGate)
        {
            if (!_consumedAuthorizations.Add(authorizationId)) return false;
            return true;
        }
    }
}

public sealed class AuthorizationLeaseStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<Guid, AuthorizationLease> _items = new();

    /// <summary>Issues a short-lived lease bound to an authenticated session. `now` is injectable for deterministic tests.</summary>
    public AuthorizationLease Issue(Guid sessionId, ToolRiskLevel riskCeiling, TimeSpan? ttl = null, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var lease = new AuthorizationLease(Guid.NewGuid(), sessionId, riskCeiling, timestamp, timestamp.Add(ttl ?? DefaultTtl));
        _items[lease.Id] = lease;
        return lease;
    }

    /// <summary>
    /// Fail-closed validation: unknown, revoked or expired leases return null and
    /// authorize nothing (prototype validateLease). Session/device freshness is
    /// re-evaluated per request by the host authentication + authorization policy.
    /// </summary>
    public AuthorizationLease? Validate(Guid leaseId, DateTimeOffset? now = null)
    {
        if (!_items.TryGetValue(leaseId, out var lease)) return null;
        var timestamp = now ?? DateTimeOffset.UtcNow;
        if (lease.Revoked || lease.ExpiresAt <= timestamp) return null;
        return lease;
    }

    /// <summary>Marks the lease revoked with a reason; a revoked lease can never validate again.</summary>
    public bool Revoke(Guid leaseId, string reason)
    {
        if (!_items.TryGetValue(leaseId, out var lease)) return false;
        lease.Revoked = true;
        lease.RevokedReason = reason;
        return true;
    }

    public AuthorizationLease? Get(Guid leaseId) => _items.TryGetValue(leaseId, out var lease) ? lease : null;
}
