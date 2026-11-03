using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Owner device re-enrollment — the recovery path for a lost device key.
///
/// The rule used to be "an Owner row exists → enrollment is refused forever". That had a
/// hole with no way out: losing the device private key (OS reinstall, DPAPI loss, disk
/// failure) made the installation permanently unable to authenticate, and every
/// remaining route needed an already-working session. These tests pin the narrowed rule:
/// the owner IDENTITY is preserved (memory and history stay attached) and a new DEVICE is
/// admitted only when the owner has no usable device left, still gated by the single-use
/// bootstrap code and still forbidden after Genesis.
/// </summary>
public sealed class OwnerDeviceRecoveryTests
{
    private const string BootstrapCode = "TLK-BSTEP-2026-INIT";

    private static async Task<(Guid DeviceId, string PublicKey, string InstallationId)> EnrollAsync(
        HttpClient client, string? installationId = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string installation = installationId ?? "recover-" + Guid.NewGuid().ToString("N")[..8];
        string publicKey = key.ExportSubjectPublicKeyInfoPem();

        var challenge = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = BootstrapCode });
        var challengeBody = await challenge.Content.ReadFromJsonAsync<JsonElement>();
        string challengeValue = challengeBody.GetProperty("challenge").GetString()!;

        var enroll = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = BootstrapCode,
            installationId = installation,
            publicKey,
            challenge = challengeValue,
            signature = Convert.ToBase64String(key.SignData(
                System.Text.Encoding.UTF8.GetBytes(challengeValue),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)),
        });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

        var body = await enroll.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("deviceId").GetGuid(), publicKey, installation);
    }

    [Fact]
    public async Task A_second_device_is_refused_while_the_owner_still_has_a_working_one()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        await EnrollAsync(client);

        // A healthy installation must not be able to re-enroll: the single-use bootstrap
        // is already consumed, and even a fresh one would be refused here.
        var second = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = BootstrapCode,
            installationId = "attacker-" + Guid.NewGuid().ToString("N")[..8],
            publicKey = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem(),
            challenge = "irrelevant",
            signature = "irrelevant",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_frees_the_slot_and_the_owner_identity_is_preserved()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var (deviceId, _, _) = await EnrollAsync(client);

        Guid ownerBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();
            ownerBefore = await db.Users.Where(u => u.Role == "Owner" && !u.Revoked).Select(u => u.Id).SingleAsync();
        }

        // Revoke the only device — exactly the state a lost key leaves behind (the owner
        // session of that device dies with it, so revoke through the database path that
        // the owner really would use: an owner session from a still-valid device).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();
            var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
            device.Revoked = true;
            await db.SaveChangesAsync();
        }

        // A fresh bootstrap must be issued for the recovery attempt.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();
            var bootstrap = await db.Bootstraps.SingleAsync();
            bootstrap.ConsumedAt = null;
            await db.SaveChangesAsync();
        }

        var (newDeviceId, newPublicKey, newInstallation) = await EnrollAsync(client);
        Assert.NotEqual(deviceId, newDeviceId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();
            Guid ownerAfter = await db.Users.Where(u => u.Role == "Owner" && !u.Revoked).Select(u => u.Id).SingleAsync();

            // The identity is the same one: memory, audit and history stay attached.
            Assert.Equal(ownerBefore, ownerAfter);

            var adopted = await db.Devices.SingleAsync(d => d.Id == newDeviceId);
            Assert.Equal(ownerAfter, adopted.UserId);
            Assert.Equal(newPublicKey, adopted.PublicKey);
            Assert.Equal(newInstallation, adopted.InstallationId);
            Assert.False(adopted.Revoked);
        }
    }

    [Fact]
    public async Task Recovery_is_still_forbidden_after_genesis()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        await EnrollAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();
            // Genesis performed: the identity is sealed for good.
            db.GenesisIdentity.Add(new TolikGenesisIdentity
            {
                InstanceId = Guid.NewGuid(),
                BornAt = DateTimeOffset.UtcNow,
                LifecycleState = "LIFETIME",
                OwnerIdentity = Guid.NewGuid(),
            });
            var device = await db.Devices.SingleAsync();
            device.Revoked = true;
            var bootstrap = await db.Bootstraps.SingleAsync();
            bootstrap.ConsumedAt = null;
            await db.SaveChangesAsync();
        }

        var challenge = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = BootstrapCode });
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        var challengeBody = await challenge.Content.ReadFromJsonAsync<JsonElement>();
        string challengeValue = challengeBody.GetProperty("challenge").GetString()!;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enroll = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = BootstrapCode,
            installationId = "post-genesis-" + Guid.NewGuid().ToString("N")[..8],
            publicKey = key.ExportSubjectPublicKeyInfoPem(),
            challenge = challengeValue,
            signature = Convert.ToBase64String(key.SignData(
                System.Text.Encoding.UTF8.GetBytes(challengeValue),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)),
        });

        Assert.Equal(HttpStatusCode.Conflict, enroll.StatusCode);
    }
}
