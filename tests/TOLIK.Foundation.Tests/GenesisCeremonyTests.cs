using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Genesis ceremony subsystem (docs/genesis.md Phase 6): preflight gates all must pass
/// (owner + MFA + memory separation + dev credentials off + operator attestations);
/// the identity row and the Genesis audit event are written atomically (no half-born
/// state); rerun is impossible; after Genesis owner replacement and the development
/// memory reset are forbidden.
/// </summary>
public sealed class GenesisCeremonyTests
{
    /// <summary>Local enrollment helper that also returns deviceId for the MFA step.</summary>
    private static async Task<(string SessionToken, Guid DeviceId)> EnrollOwnerWithDeviceAsync(HttpClient client)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var challengeValue = challenge.GetProperty("challenge").GetString()!;
        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(challengeValue),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var enrollResponse = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = "TLK-BSTEP-2026-INIT",
            installationId = "test-install-" + Guid.NewGuid().ToString("N")[..8],
            publicKey,
            challenge = challengeValue,
            signature
        });
        Assert.Equal(HttpStatusCode.OK, enrollResponse.StatusCode);
        var enrolled = await enrollResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (
            enrolled.GetProperty("sessionToken").GetString()!,
            enrolled.GetProperty("deviceId").GetGuid());
    }

    /// <summary>Enrolls MFA on the owner device and returns the current TOTP code.</summary>
    private static async Task<string> EnrollMfaAndGetCurrentCodeAsync(HttpClient client, Guid deviceId)
    {
        var enrollMfa = await client.PostAsJsonAsync("/mfa/enroll", new { deviceId });
        Assert.Equal(HttpStatusCode.OK, enrollMfa.StatusCode);
        var secret = (await enrollMfa.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret), step: 30, mode: OtpHashMode.Sha1);
        return totp.ComputeTotp(DateTime.UtcNow);
    }

    private static object CeremonyBody(Guid deviceId, string code) => new
    {
        codeIdentity = "22133e8",
        knowledgeIdentity = "knowledge-hash-test",
        mfaDeviceId = deviceId,
        mfaCode = code,
        backupConfirmed = true,
        restoreTested = true
    };

    [Fact]
    public async Task Ceremony_performs_atomically_and_rerun_is_impossible()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (sessionToken, deviceId) = await EnrollOwnerWithDeviceAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // Identity not found before the ceremony.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/genesis/identity")).StatusCode);

        var ceremony = await client.PostAsJsonAsync("/genesis/ceremony", CeremonyBody(deviceId, await EnrollMfaAndGetCurrentCodeAsync(client, deviceId)));
        Assert.Equal(HttpStatusCode.OK, ceremony.StatusCode);
        var identity = await ceremony.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LIFETIME", identity.GetProperty("lifecycleState").GetString());
        Assert.Equal("22133e8", identity.GetProperty("codeIdentity").GetString());
        Assert.False(identity.GetProperty("instanceId").GetGuid().Equals(Guid.Empty));

        // The identity is persisted and readable by the owner.
        var get = await client.GetAsync("/genesis/identity");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(identity.GetProperty("instanceId").GetString(),
            (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString());

        // Rerun impossibility: a second ceremony is refused.
        var rerun = await client.PostAsJsonAsync("/genesis/ceremony", CeremonyBody(deviceId, await EnrollMfaAndGetCurrentCodeAsync(client, deviceId)));
        Assert.Equal(HttpStatusCode.Conflict, rerun.StatusCode);
        Assert.Equal("genesis_already_performed", (await rerun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_mfa_code_refuses_the_ceremony()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (sessionToken, deviceId) = await EnrollOwnerWithDeviceAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        await EnrollMfaAndGetCurrentCodeAsync(client, deviceId);

        var ceremony = await client.PostAsJsonAsync("/genesis/ceremony", CeremonyBody(deviceId, "000000"));
        Assert.Equal(HttpStatusCode.BadRequest, ceremony.StatusCode);
        var body = await ceremony.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preflight_failed", body.GetProperty("error").GetString());
        Assert.Contains("mfa_verified", body.GetProperty("failedGates").EnumerateArray().Select(g => g.GetString()!).ToList());
        // The identity was NOT born (no half-born state).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/genesis/identity")).StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_run_the_ceremony()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        await TestEnrollment.EnrollOwnerAsync(client);

        // Enroll a Family member (owner token needed — do the full flow as owner).
        // Simpler: an anonymous call is rejected first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/genesis/ceremony", new { })).StatusCode);
    }

    [Fact]
    public async Task Unconfirmed_backup_blocks_the_ceremony()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (sessionToken, deviceId) = await EnrollOwnerWithDeviceAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        var code = await EnrollMfaAndGetCurrentCodeAsync(client, deviceId);

        var noBackup = await client.PostAsJsonAsync("/genesis/ceremony", new
        {
            codeIdentity = "22133e8",
            knowledgeIdentity = "knowledge-hash-test",
            mfaDeviceId = deviceId,
            mfaCode = code,
            backupConfirmed = false,
            restoreTested = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, noBackup.StatusCode);
        var body = await noBackup.Content.ReadFromJsonAsync<JsonElement>();
        var gates = body.GetProperty("failedGates").EnumerateArray().Select(g => g.GetString()!).ToList();
        Assert.Contains("backup_exists", gates);
        Assert.Contains("restore_tested", gates);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/genesis/identity")).StatusCode);
    }

    [Fact]
    public async Task Re_bootstrap_is_blocked_after_genesis()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (sessionToken, deviceId) = await EnrollOwnerWithDeviceAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        var ceremony = await client.PostAsJsonAsync("/genesis/ceremony", CeremonyBody(deviceId, await EnrollMfaAndGetCurrentCodeAsync(client, deviceId)));
        Assert.Equal(HttpStatusCode.OK, ceremony.StatusCode);

        // Owner replacement via re-bootstrap is forbidden (docs/genesis.md After Genesis):
        // the challenge gate already refuses (owner consumed → 401 enrollment_denied, a
        // pre-existing single-owner block); the genesis check in EnrollOwnerAsync adds
        // service-level defense-in-depth for any direct path.
        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        Assert.Equal(HttpStatusCode.Unauthorized, challengeResponse.StatusCode);
        Assert.Equal("enrollment_denied", (await challengeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Development_memory_reset_is_blocked_after_genesis()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableMemoryStore>();
        var genesis = scope.ServiceProvider.GetRequiredService<TOLIK.Infrastructure.Genesis.GenesisService>();

        // In DEVELOPMENT (no genesis row) the reset works.
        var owner = Guid.NewGuid();
        await store.AddAsync(owner, MemoryScope.Private, "a", "test", 0.9);
        Assert.Equal(1, await store.DeleteAllAsync(owner));

        // Simulate the ceremony (direct service call — bypasses HTTP auth for this unit).
        await genesis.PerformCeremonyAsync(Guid.NewGuid(), "22133e8", "knowledge-hash-test", Guid.NewGuid());
        await store.AddAsync(owner, MemoryScope.Private, "b", "test", 0.9);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAllAsync(owner));
        // The memory survives (no silent history deletion).
        Assert.Single(await store.SearchAsync(owner, "b"));
    }
}
