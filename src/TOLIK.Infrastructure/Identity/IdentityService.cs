using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using OtpNet;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Infrastructure.Identity;

public sealed record SessionPrincipal(Guid UserId, Guid DeviceId, Guid SessionId, string Role);
public sealed record DeviceChallenge(string Token, DateTimeOffset ExpiresAt);
public sealed record MfaEnrollmentResult(string Secret, string QrCodeUri);
public sealed record MfaVerificationResult(bool Success, string? Error);

public sealed class IdentityService(IDbContextFactory<TolikDbContext> contextFactory, ILogger<IdentityService> logger)
{
    public async Task EnsureBootstrapAsync(string code, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Bootstrap code is required.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Bootstraps.AnyAsync(cancellationToken)) return;
        db.Bootstraps.Add(new TolikBootstrap { Id = Guid.NewGuid(), CodeHash = HashToken(code), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime) });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent first-time challenges both passed the Any-check — the unique
            // CodeHash index rejects the second insert. If the winner's row exists, the
            // bootstrap is ensured and the request may proceed (production race fix).
            if (!await db.Bootstraps.AnyAsync(CancellationToken.None)) throw;
        }
    }

    public async Task<DeviceChallenge?> IssueDeviceChallengeAsync(string bootstrapCode, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bootstrapCode)) return null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var bootstrap = await db.Bootstraps.SingleOrDefaultAsync(cancellationToken);
        if (bootstrap is null || bootstrap.ConsumedAt is not null || bootstrap.ExpiresAt <= DateTimeOffset.UtcNow || !FixedEquals(bootstrap.CodeHash, HashToken(bootstrapCode))) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = new TolikDeviceChallenge { Id = Guid.NewGuid(), BootstrapId = bootstrap.Id, TokenHash = HashToken(token), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime) };
        db.DeviceChallenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);
        return new DeviceChallenge(token, challenge.ExpiresAt);
    }

    public async Task<(TolikUser User, TolikDevice Device, Guid SessionId, string SessionToken)> EnrollOwnerAsync(string bootstrapCode, string installationId, string publicKey, string challengeToken, string signatureBase64, TimeSpan sessionLifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bootstrapCode) || string.IsNullOrWhiteSpace(installationId) || installationId.Length > 128 || !DeviceChallengeVerifier.IsP256PublicKey(publicKey) || string.IsNullOrWhiteSpace(challengeToken) || string.IsNullOrWhiteSpace(signatureBase64)) throw new UnauthorizedAccessException("Enrollment denied.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = await db.Bootstraps.SingleOrDefaultAsync(cancellationToken);
        var challenge = await db.DeviceChallenges.SingleOrDefaultAsync(x => x.TokenHash == HashToken(challengeToken), cancellationToken);
        var valid = bootstrap is not null
            && challenge is not null
            && challenge.BootstrapId == bootstrap.Id
            && bootstrap.ConsumedAt is null
            && bootstrap.ExpiresAt > now
            && challenge.ConsumedAt is null
            && challenge.ExpiresAt > now
            && FixedEquals(bootstrap.CodeHash, HashToken(bootstrapCode))
            && DeviceChallengeVerifier.Verify(challengeToken, publicKey, signatureBase64);
        if (!valid)
        {
            logger.LogWarning("Owner enrollment denied during challenge validation. bootstrap={BootstrapPresent}, challenge={ChallengePresent}, binding={BindingValid}, bootstrapActive={BootstrapActive}, challengeActive={ChallengeActive}, codeValid={CodeValid}, signatureValid={SignatureValid}",
                bootstrap is not null, challenge is not null, challenge is not null && bootstrap is not null && challenge.BootstrapId == bootstrap.Id,
                bootstrap is not null && bootstrap.ConsumedAt is null && bootstrap.ExpiresAt > now,
                challenge is not null && challenge.ConsumedAt is null && challenge.ExpiresAt > now,
                bootstrap is not null && FixedEquals(bootstrap.CodeHash, HashToken(bootstrapCode)),
                DeviceChallengeVerifier.Verify(challengeToken, publicKey, signatureBase64));
            throw new UnauthorizedAccessException("Enrollment denied.");
        }
        var verifiedBootstrap = bootstrap!;
        var verifiedChallenge = challenge!;
        if (await db.Users.AnyAsync(x => x.Role == TolikRoles.OwnerName && !x.Revoked, cancellationToken)) throw new InvalidOperationException("Owner enrollment has already been consumed.");
        var user = new TolikUser { Id = Guid.NewGuid(), Role = TolikRoles.OwnerName, CreatedAt = now };
        var device = new TolikDevice { Id = Guid.NewGuid(), UserId = user.Id, InstallationId = installationId, PublicKey = publicKey, CreatedAt = now };
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new TolikSession { Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id, TokenHash = HashToken(token), CreatedAt = now, ExpiresAt = now.Add(sessionLifetime) };
        db.AddRange(user, device, session); verifiedBootstrap.ConsumedAt = now; verifiedChallenge.ConsumedAt = now;
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return (user, device, session.Id, token);
    }

    /// <summary>
    /// Dev-convenience session minting for the same-machine UI (explicit opt-in via
    /// TOLIK_DEV_AUTO_TOKEN + loopback check at the endpoint). Reuses the exact
    /// session shape as enrollment; never enabled on shared/VPS hosts.
    /// Returns null when no enrolled Owner device exists yet (run bootstrap first).
    /// </summary>
    public async Task<(Guid SessionId, string SessionToken, DateTimeOffset ExpiresAt)?> IssueLocalDevTokenAsync(TimeSpan sessionLifetime, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var user = await db.Users.SingleOrDefaultAsync(x => x.Role == TolikRoles.OwnerName && !x.Revoked, cancellationToken);
        if (user is null) return null;
        var device = await db.Devices.Where(x => x.UserId == user.Id && !x.Revoked).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (device is null) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new TolikSession { Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id, TokenHash = HashToken(token), CreatedAt = now, ExpiresAt = now.Add(sessionLifetime) };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Dev auto-token issued for local UI. Disable TOLIK_DEV_AUTO_TOKEN outside single-user dev machines.");
        return (session.Id, token, session.ExpiresAt);
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) { await using var db = await contextFactory.CreateDbContextAsync(cancellationToken); var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken); if (session is null) return false; session.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken); return true; }
    public async Task<bool> OwnsSessionAsync(SessionPrincipal principal, Guid sessionId, CancellationToken cancellationToken = default) { await using var db = await contextFactory.CreateDbContextAsync(cancellationToken); return await db.Sessions.AnyAsync(x => x.Id == sessionId && x.UserId == principal.UserId, cancellationToken); }
    public async Task<bool> OwnsDeviceAsync(SessionPrincipal principal, Guid deviceId, CancellationToken cancellationToken = default) { await using var db = await contextFactory.CreateDbContextAsync(cancellationToken); return await db.Devices.AnyAsync(x => x.Id == deviceId && x.UserId == principal.UserId, cancellationToken); }
    public async Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) { await using var db = await contextFactory.CreateDbContextAsync(cancellationToken); var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken); if (device is null) return false; device.Revoked = true; await db.Sessions.Where(x => x.DeviceId == deviceId && x.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow), cancellationToken); await db.SaveChangesAsync(cancellationToken); return true; }
    public async Task<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.TokenHash == HashToken(token), cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == session.DeviceId, cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == session.UserId, cancellationToken);
        if (device is null || user is null || device.Revoked || user.Revoked) return null;

        // Sliding expiration (§13): active personal-assistant sessions keep themselves
        // alive; idle sessions still expire. Only pushes forward, never extends a
        // revoked/near-zero session beyond its original absolute intent.
        var now = DateTimeOffset.UtcNow;
        var remaining = session.ExpiresAt - now;
        if (remaining < TimeSpan.FromHours(1))
        {
            var total = session.ExpiresAt - session.CreatedAt;
            session.ExpiresAt = now.Add(total > TimeSpan.Zero ? total : TimeSpan.FromDays(7));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new SessionPrincipal(user.Id, device.Id, session.Id, user.Role);
    }

    /// <summary>
    /// Enrolls a TOTP secret for OwnerStrongAuth MFA on a device.
    /// Returns the secret (Base32) and an otpauth:// URI for QR code generation.
    /// </summary>
    public async Task<MfaEnrollmentResult> EnrollMfaAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) throw new ArgumentException("Device not found.", nameof(deviceId));

        // Generate a 20-byte (160-bit) secret for TOTP (Base32 encoded = 32 chars)
        var secretBytes = RandomNumberGenerator.GetBytes(20);
        var secret = Base32Encoding.ToString(secretBytes);
        device.TotpSecret = secret;
        await db.SaveChangesAsync(cancellationToken);

        // Build otpauth:// URI for QR code
        var qrCodeUri = $"otpauth://totp/TOLIK:{Environment.UserName}?secret={secret}&issuer=TOLIK&algorithm=SHA1&digits=6&period=30";
        return new MfaEnrollmentResult(secret, qrCodeUri);
    }

    /// <summary>
    /// Verifies a TOTP code for OwnerStrongAuth MFA.
    /// Returns success/failure with optional error message.
    /// </summary>
    public async Task<MfaVerificationResult> VerifyMfaAsync(Guid deviceId, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit))
            return new MfaVerificationResult(false, "Invalid code format (must be 6 digits).");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null)
            return new MfaVerificationResult(false, "Device not found.");
        if (string.IsNullOrWhiteSpace(device.TotpSecret))
            return new MfaVerificationResult(false, "MFA not enrolled on this device.");

        // Verify TOTP with 1-step window (±30 seconds) for clock skew tolerance
        var secretBytes = Base32Encoding.ToBytes(device.TotpSecret);
        var totp = new Totp(secretBytes, step: 30, mode: OtpHashMode.Sha1);
        var isValid = totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);

        if (!isValid)
            return new MfaVerificationResult(false, "Invalid or expired code.");

        // Update last verified timestamp
        await using var db2 = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device2 = await db2.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device2 is not null)
        {
            device2.MfaLastVerifiedAt = DateTimeOffset.UtcNow;
            await db2.SaveChangesAsync(cancellationToken);
        }
        await db2.DisposeAsync();

        return new MfaVerificationResult(true, null);
    }

    private static bool FixedEquals(string leftHex, string rightHex) { try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(leftHex), Convert.FromHexString(rightHex)); } catch (FormatException) { return false; } }
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}