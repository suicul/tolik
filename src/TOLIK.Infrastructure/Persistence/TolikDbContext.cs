using Microsoft.EntityFrameworkCore;

namespace TOLIK.Infrastructure.Persistence;

public sealed class TolikDbContext(DbContextOptions<TolikDbContext> options) : DbContext(options)
{
    public DbSet<SystemRecord> SystemRecords => Set<SystemRecord>();
    public DbSet<TolikUser> Users => Set<TolikUser>();
    public DbSet<TolikDevice> Devices => Set<TolikDevice>();
    public DbSet<TolikSession> Sessions => Set<TolikSession>();
    public DbSet<TolikBootstrap> Bootstraps => Set<TolikBootstrap>();
    public DbSet<TolikDeviceChallenge> DeviceChallenges => Set<TolikDeviceChallenge>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<TolikTaskEntity> Tasks => Set<TolikTaskEntity>();
    public DbSet<TolikMemoryEntity> Memories => Set<TolikMemoryEntity>();
    public DbSet<TolikLearnedRuleEntity> LearnedRules => Set<TolikLearnedRuleEntity>();
    public DbSet<TolikProactiveEntity> Proactive => Set<TolikProactiveEntity>();

    public DbSet<TolikGenesisIdentity> GenesisIdentity => Set<TolikGenesisIdentity>();
    public DbSet<CoreLeaseEntity> CoreLeases => Set<CoreLeaseEntity>();

    public DbSet<BaselineEntity> Baselines => Set<BaselineEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureAuditAppendOnly()
    {
        if (ChangeTracker.Entries<AuditEvent>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit events are append-only.");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<SystemRecord>();
        record.ToTable("system_records");
        record.HasKey(x => x.Key);
        record.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
        record.Property(x => x.Value).HasColumnName("value").HasColumnType("text");
        record.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        var user = modelBuilder.Entity<TolikUser>();
        user.ToTable("tolik_users");
        user.HasKey(x => x.Id);
        user.Property(x => x.Role).HasMaxLength(32).IsRequired();
        user.HasIndex(x => x.Role).IsUnique().HasFilter("\"Role\" = 'Owner'");

        var device = modelBuilder.Entity<TolikDevice>();
        device.ToTable("tolik_devices");
        device.HasKey(x => x.Id);
        device.Property(x => x.InstallationId).HasMaxLength(128).IsRequired();
        device.Property(x => x.PublicKey).HasColumnType("text").IsRequired();
        device.HasIndex(x => x.InstallationId).IsUnique();
        device.HasOne<TolikUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        var session = modelBuilder.Entity<TolikSession>();
        session.ToTable("tolik_sessions");
        session.HasKey(x => x.Id);
        session.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        session.HasIndex(x => x.TokenHash).IsUnique();
        session.HasOne<TolikUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<TolikDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);

        var bootstrap = modelBuilder.Entity<TolikBootstrap>();
        bootstrap.ToTable("tolik_bootstraps");
        bootstrap.HasKey(x => x.Id);
        bootstrap.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
        bootstrap.HasIndex(x => x.CodeHash).IsUnique();

        var challenge = modelBuilder.Entity<TolikDeviceChallenge>();
        challenge.ToTable("tolik_device_challenges");
        challenge.HasKey(x => x.Id);
        challenge.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        challenge.HasIndex(x => x.TokenHash).IsUnique();
        challenge.HasOne<TolikBootstrap>().WithMany().HasForeignKey(x => x.BootstrapId).OnDelete(DeleteBehavior.Cascade);

        var audit = modelBuilder.Entity<AuditEvent>();
        audit.ToTable("audit_events");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.EventType).HasMaxLength(128).IsRequired();
        audit.Property(x => x.Decision).HasMaxLength(32).IsRequired();
        audit.Property(x => x.Reason).HasMaxLength(512).IsRequired();
        audit.Property(x => x.UserId).HasMaxLength(64);
        audit.Property(x => x.DeviceId).HasMaxLength(64);
        audit.Property(x => x.SessionId).HasMaxLength(64);
        audit.Property(x => x.Capability).HasMaxLength(64);
        audit.Property(x => x.Risk).HasMaxLength(32);
        audit.Property(x => x.Resource).HasMaxLength(512);
        audit.Property(x => x.Result).HasMaxLength(128);
        audit.Property(x => x.Severity).HasMaxLength(32).IsRequired();
        audit.Property(x => x.PreviousHash).HasMaxLength(64);
        audit.Property(x => x.IntegrityHash).HasMaxLength(64).IsRequired();
        audit.HasIndex(x => x.OccurredAt);

        var task = modelBuilder.Entity<TolikTaskEntity>();
        task.ToTable("tolik_tasks");
        task.HasKey(x => x.Id);
        task.Property(x => x.Description).HasMaxLength(4096).IsRequired();
        task.Property(x => x.Status).HasMaxLength(32).IsRequired();
        task.HasIndex(x => new { x.UserId, x.CreatedAt });
        var memory = modelBuilder.Entity<TolikMemoryEntity>();
        memory.ToTable("tolik_memories");
        memory.HasKey(x => x.Id);
        memory.Property(x => x.Scope).HasMaxLength(32).IsRequired();
        memory.Property(x => x.Content).HasMaxLength(8192).IsRequired();
        memory.Property(x => x.Source).HasMaxLength(256).IsRequired();
        memory.Property(x => x.Origin).HasMaxLength(32).IsRequired().HasDefaultValue("lived");
        memory.HasIndex(x => new { x.OwnerId, x.Scope, x.CreatedAt });
        var rule = modelBuilder.Entity<TolikLearnedRuleEntity>();
        rule.ToTable("tolik_learned_rules");
        rule.HasKey(x => x.Id);
        rule.Property(x => x.Rule).HasMaxLength(8192).IsRequired();
        rule.Property(x => x.Source).HasMaxLength(256).IsRequired();
        rule.HasIndex(x => new { x.OwnerId, x.Confidence, x.CreatedAt });
        var proactive = modelBuilder.Entity<TolikProactiveEntity>();
        proactive.ToTable("tolik_proactive");
        proactive.HasKey(x => x.Id);
        proactive.Property(x => x.Text).HasMaxLength(2048).IsRequired();
        proactive.Property(x => x.Priority).HasMaxLength(16).IsRequired();
        proactive.Property(x => x.Reason).HasMaxLength(512).IsRequired();
        proactive.HasIndex(x => new { x.UserId, x.DeliveredAt, x.DueAt });
        var genesis = modelBuilder.Entity<TolikGenesisIdentity>();
        genesis.ToTable("tolik_genesis_identity");
        genesis.HasKey(x => x.InstanceId);
        genesis.Property(x => x.CodeIdentity).HasMaxLength(64).IsRequired();
        genesis.Property(x => x.KnowledgeIdentity).HasMaxLength(64).IsRequired();
        genesis.Property(x => x.LifecycleState).HasMaxLength(32).IsRequired();
        // Rerun impossibility (docs/genesis.md): the singleton sentinel column admits
        // exactly one row — a second ceremony insert violates the unique constraint.
        genesis.HasIndex(x => x.LifecycleState).IsUnique();

        var coreLease = modelBuilder.Entity<CoreLeaseEntity>();
        coreLease.ToTable("tolik_core_leases");
        coreLease.HasKey(x => x.Id);
        coreLease.Property(x => x.Id).ValueGeneratedNever();
        coreLease.Property(x => x.ActiveHost).HasMaxLength(128).IsRequired();
        coreLease.Property(x => x.State).HasMaxLength(32).IsRequired();
        // Split-brain prevention (docs/identity-presence.md §1/IP-1): the lease table is
        // single-row — the singleton sentinel primary key (Id = 1, never database
        // generated) admits exactly one row; a second concurrent insert violates the PK,
        // mirroring the GenesisIdentity at-most-one pattern. The table is identity-
        // protected machinery, part of the protected core (self-evolution §4 protected
        // areas): never writable by tool/self-evolution paths.
    }
}
