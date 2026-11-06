using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Infrastructure.Genesis;

/// <summary>
/// Genesis preflight gates (docs/genesis.md — all must pass). Deterministic and
/// testable: dev credentials off, runtime authority, memory separation (no
/// origin=development rows in the production database), MFA verified, owner
/// authenticated. Backup/restore are operator attestations (the scripts exist and
/// run outside the ceremony) — recorded in the ceremony event, not silently assumed.
/// </summary>
public sealed record GenesisPreflightResult(bool Passed, IReadOnlyList<string> FailedGates)
{
    public static readonly GenesisPreflightResult Ok = new(true, []);
}

public sealed record GenesisCeremonyRequest(
    string? CodeIdentity,
    string? KnowledgeIdentity,
    Guid MfaDeviceId,
    string? MfaCode,
    bool BackupConfirmed,
    bool RestoreTested);

public static class GenesisPreflight
{
    public static async Task<GenesisPreflightResult> CheckAsync(
        TolikDbContext db,
        bool ownerAuthenticated,
        bool mfaVerified,
        bool devTokensEnabled,
        bool runtimeAuthority,
        bool backupConfirmed,
        bool restoreTested,
        CancellationToken cancellationToken = default)
    {
        var failed = new List<string>();
        if (!ownerAuthenticated) failed.Add("owner_authentication");
        if (!mfaVerified) failed.Add("mfa_verified");
        if (devTokensEnabled) failed.Add("dev_credentials_present");
        if (!runtimeAuthority) failed.Add("runtime_authority");
        if (!backupConfirmed) failed.Add("backup_exists");
        if (!restoreTested) failed.Add("restore_tested");
        // Memory separation (docs/genesis.md): no origin=development rows outside test
        // fixtures — the production database must hold none.
        var developmentRows = await db.Memories.CountAsync(
            x => x.Origin == "development", cancellationToken);
        if (developmentRows > 0) failed.Add($"memory_separation:{developmentRows}");
        // The Genesis seed (docs/genesis-ceremony.md Шаг 4, PLAN-ЭТАЛОН Phase 2.2): the
        // ceremony is the birth of an identity WITH its temperament and values, so a birth
        // with no seed is refused rather than silently producing a personality-less mind.
        if (!await db.GenesisSeeds.AnyAsync(cancellationToken)) failed.Add("genesis_seed_loaded");
        return failed.Count == 0 ? GenesisPreflightResult.Ok : new GenesisPreflightResult(false, failed);
    }
}

/// <summary>
/// One-time birth ceremony (docs/genesis.md, Phase 6): creates the TOLIK identity and
/// writes the Genesis audit event in a single database transaction — interruption
/// rolls back to no-half-born state. Rerun impossibility is enforced by the unique
/// LifecycleState sentinel (at most one identity row ever exists) and serialized
/// against concurrent ceremonies through the shared audit-chain advisory lock.
/// </summary>
public sealed class GenesisService(IDbContextFactory<TolikDbContext> contextFactory)
{
    public const int CeremonyVersion = 1;

    public async Task<TolikGenesisIdentity?> TryGetIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GenesisIdentity.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<TolikGenesisIdentity> PerformCeremonyAsync(
        Guid ownerIdentity, string codeIdentity, string knowledgeIdentity, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codeIdentity)) throw new ArgumentException("CodeIdentity (git commit of the verified baseline) is required.", nameof(codeIdentity));
        if (string.IsNullOrWhiteSpace(knowledgeIdentity)) throw new ArgumentException("KnowledgeIdentity (hash of the imported knowledge set) is required.", nameof(knowledgeIdentity));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Rerun impossibility (docs/genesis.md): the identity is born exactly once.
        if (await db.GenesisIdentity.AnyAsync(cancellationToken))
            throw new InvalidOperationException("genesis_already_performed");

        // The shared audit-chain advisory lock serializes the ceremony against
        // concurrent audit writes: the Genesis event lands atomically at the chain tip.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.audit-chain'))", cancellationToken);
        var previousHash = await db.AuditEvents
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.IntegrityHash)
            .FirstOrDefaultAsync(cancellationToken);

        var bornAt = DateTimeOffset.UtcNow;
        var identity = new TolikGenesisIdentity
        {
            InstanceId = Guid.NewGuid(),
            BornAt = bornAt,
            GenesisVersion = CeremonyVersion,
            CodeIdentity = codeIdentity,
            KnowledgeIdentity = knowledgeIdentity,
            LifecycleState = "LIFETIME",
            OwnerIdentity = ownerIdentity
        };
        db.GenesisIdentity.Add(identity);

        const string eventType = "genesis.ceremony";
        const string decision = "ALLOW";
        // The seed's activation flips IN THE SAME TRANSACTION as the birth (PLAN-ЭТАЛОН
        // Phase 2.2): the identity and the right to read its own seed are one atomic act.
        // If the row is absent (a direct service call that bypassed the preflight gate), the
        // ceremony still commits — but the event records the absence honestly rather than
        // pretending the mind was born with a seed it does not have.
        var seed = await db.GenesisSeeds.SingleOrDefaultAsync(x => x.Id == GenesisSeedEntity.SingletonId, cancellationToken);
        if (seed is not null) seed.Activated = true;
        var seedState = seed is null ? "seed:absent" : "seed:activated";
        var reason = $"instance_born:{identity.InstanceId:D};owner:{ownerIdentity:D};{seedState}";
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = bornAt,
            CorrelationId = correlationId,
            EventType = eventType,
            Decision = decision,
            Reason = reason,
            UserId = ownerIdentity.ToString("D"),
            PreviousHash = previousHash,
            IntegrityHash = AuditWriter.ComputeIntegrityHash(correlationId, bornAt, eventType, decision, reason, previousHash)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return identity;
    }
}
