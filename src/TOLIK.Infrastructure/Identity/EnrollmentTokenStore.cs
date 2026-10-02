using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TOLIK.Infrastructure.Identity;

public enum EnrollmentTokenRole
{
    TrustedGuest,
    Family
}

public sealed record EnrollmentToken(
    Guid Id,
    EnrollmentTokenRole Role,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public bool Consumed { get; internal set; }
}

/// <summary>
/// Owner-issued enrollment tokens for trusted-person enrollment (docs/SECURITY.md §18,
/// ported from interface/identity.js createEnrollmentToken): the Owner mints a
/// short-lived (10-minute default) single-use token for an explicit role; the holder
/// enrolls once with it. Tokens are stored by SHA-256 hash (the raw token is returned
/// exactly once and never stored), are single-use and expire — an expired or consumed
/// token authorizes nothing. In-memory per-process (restart clears outstanding tokens).
/// </summary>
public sealed class EnrollmentTokenStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, EnrollmentToken> _items = new();

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Mints a single-use enrollment token; the raw token is returned once.</summary>
    public (EnrollmentToken Record, string Token) Issue(
        EnrollmentTokenRole role, DateTimeOffset now, TimeSpan? ttl = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var record = new EnrollmentToken(
            Guid.NewGuid(), role, HashToken(token), now, now.Add(ttl ?? DefaultTtl));
        _items[record.Id] = record;
        return (record, token);
    }

    /// <summary>
    /// Single-use consumption by raw token: succeeds only for an unconsumed, non-expired
    /// token whose hash matches; consumed/expired/unknown tokens authorize nothing.
    /// </summary>
    public EnrollmentToken? Consume(string token, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token);
        var record = _items.Values.FirstOrDefault(x => x.TokenHash == hash);
        if (record is null || record.Consumed || record.ExpiresAt <= now) return null;
        record.Consumed = true;
        return record;
    }
}
