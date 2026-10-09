namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Core lease — the single ACTIVE Core claim (docs/identity-presence.md §1/IP-1):
/// ActiveHost / RuntimeInstanceId / Epoch (monotonic) / LeaseExpiration / State.
/// This table is identity-protected machinery and part of the protected core
/// (self-evolution §4 protected areas): the lease is what technically prevents
/// split-brain, so it must never be writable by tool/self-evolution paths.
/// At most ONE row may ever exist: the singleton sentinel primary key (Id = 1) —
/// a second concurrent insert violates the primary key, mirroring the
/// GenesisIdentity at-most-one pattern.
/// </summary>
public sealed class CoreLeaseEntity
{
    public long Id { get; set; }
    public long Epoch { get; set; }
    public string ActiveHost { get; set; } = string.Empty;
    public Guid RuntimeInstanceId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset LeaseExpiration { get; set; }
    public string State { get; set; } = "ACTIVE";
}
