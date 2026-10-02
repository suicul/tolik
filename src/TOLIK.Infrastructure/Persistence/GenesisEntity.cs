namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Genesis identity — the one-time birth ceremony record (docs/genesis.md, Phase 6).
/// At most ONE row may ever exist: instance_id is born once, never reused; the table's
/// existence with a row means the instance is in LIFETIME and Genesis cannot rerun.
/// The ceremony writes this row and the Genesis audit event in a single transaction —
/// interruption rolls back to no-half-born state.
/// </summary>
public sealed class TolikGenesisIdentity
{
    public Guid InstanceId { get; set; }
    public DateTimeOffset BornAt { get; set; }
    public int GenesisVersion { get; set; } = 1;
    public string CodeIdentity { get; set; } = string.Empty;
    public string KnowledgeIdentity { get; set; } = string.Empty;
    public string LifecycleState { get; set; } = "LIFETIME";
    public Guid OwnerIdentity { get; set; }
}
