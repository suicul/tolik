namespace TOLIK.Infrastructure.Persistence;

public sealed class TolikBootstrap
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class TolikDeviceChallenge
{
    public Guid Id { get; set; }
    public Guid BootstrapId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class TolikUser
{
    public Guid Id { get; set; }
    public string Role { get; set; } = "Owner";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Revoked { get; set; }
}

public sealed class TolikDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string InstallationId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    /// <summary>TOTP secret for OwnerStrongAuth MFA (Base32 encoded). Null = MFA not enrolled.</summary>
    public string? TotpSecret { get; set; }
    /// <summary>Timestamp of last successful MFA verification.</summary>
    public DateTimeOffset? MfaLastVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Revoked { get; set; }
}

public sealed class TolikSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class TolikTaskEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

public sealed class TolikMemoryEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Scope { get; set; } = "Private";
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set when the memory was folded into a compaction summary; content is kept for audit.</summary>
    public DateTimeOffset? CompactedAt { get; set; }
}

public sealed class TolikLearnedRuleEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Rule { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}