namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// A one-time challenge a known device signs to prove it still holds its private key.
///
/// Separate from <see cref="TolikDeviceChallenge"/> on purpose: that one is bound to the
/// single-use bootstrap and therefore dies with enrollment. Session renewal happens
/// repeatedly for the life of an installation, so it needs its own short-lived,
/// independently consumable token. Single use is what makes the proof non-replayable —
/// a captured signature is worthless once the challenge behind it is consumed.
/// </summary>
public sealed class TolikDeviceSessionChallenge
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
