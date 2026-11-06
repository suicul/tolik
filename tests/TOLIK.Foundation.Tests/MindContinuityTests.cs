using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;
using Xunit;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Phase 2 of docs/ПЛАН-ЭТАЛОН.md — the mind's CONTINUITY (blockers Б1 and Б3).
///
/// Before this suite existed, GenesisSeedRuntime.Load/Activate had ZERO production call
/// sites: the ceremony minted the identity row and the audit event, and the mind went on
/// reporting "NotSeeded" forever — a born identity that could never say it was born.
/// And MindState lived in a private field of a singleton, so every Core restart silently
/// reset boredom/curiosity/attention and, worse, the consecutive-failure streak that the
/// mind uses to surface its own trouble (MI-7).
///
/// These tests fail against the old code by construction: the endpoint 404s, the seed
/// never leaves NotSeeded, and the store types do not exist.
/// </summary>
public sealed class MindContinuityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- helpers

    private static async Task<(string SessionToken, Guid DeviceId)> EnrollOwnerAsync(HttpClient client)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        var challengeValue = (await challengeResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challenge").GetString()!;
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
        return (enrolled.GetProperty("sessionToken").GetString()!, enrolled.GetProperty("deviceId").GetGuid());
    }

    private static async Task<string> EnrollMfaAndGetCodeAsync(HttpClient client, Guid deviceId)
    {
        var enrollMfa = await client.PostAsJsonAsync("/mfa/enroll", new { deviceId });
        Assert.Equal(HttpStatusCode.OK, enrollMfa.StatusCode);
        var secret = (await enrollMfa.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret), step: 30, mode: OtpHashMode.Sha1);
        return totp.ComputeTotp(DateTime.UtcNow);
    }

    private static async Task<string> MindSeedStatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/mind/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    // ------------------------------------------------- 2.1 / 2.5 seed loading

    /// <summary>
    /// An invalid seed is rejected at the boundary. The data record already knows how to
    /// validate itself (GenesisSeedData.IsValid) — the endpoint must not accept what the
    /// domain rejects, or the invalid seed only explodes later inside Load().
    /// </summary>
    [Fact]
    public async Task Seed_endpoint_rejects_an_invalid_seed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (token, deviceId) = await EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var code = await EnrollMfaAndGetCodeAsync(client, deviceId);

        // Empty temperament: fails GenesisSeedData.IsValid.
        var response = await client.PostAsJsonAsync("/genesis/seed", new
        {
            temperament = "",
            coreValues = new[] { "honesty" },
            mfaDeviceId = deviceId,
            mfaCode = code
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("NotSeeded", await MindSeedStatusAsync(client));
    }

    /// <summary>
    /// A valid seed is LOADED but not ACTIVATED: the status becomes Seeded, and the mind
    /// still reads nothing. This is the pre-Genesis honest boundary from docs/mind.md §7 —
    /// the ceremony fills the data, only the ceremony's touchpoint flips the activation.
    /// </summary>
    [Fact]
    public async Task Seed_endpoint_loads_a_valid_seed_without_activating_it()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (token, deviceId) = await EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var code = await EnrollMfaAndGetCodeAsync(client, deviceId);

        var response = await client.PostAsJsonAsync("/genesis/seed", new
        {
            temperament = "calm-technical-dry-humor",
            coreValues = new[] { "honesty", "non-destruction" },
            mfaDeviceId = deviceId,
            mfaCode = code
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Seeded", await MindSeedStatusAsync(client));

        // The boundary that matters: Seeded is NOT Activated.
        var seed = factory.Services.GetRequiredService<MindRuntime>().GenesisSeed;
        Assert.Equal(SeedStatus.Seeded, seed.Status);
        Assert.Null(seed.Snapshot());
    }

    /// <summary>
    /// 2.5 — the mind must not read the seed before the activation. Seeding alone must
    /// leave Snapshot() null, even though the data is present and valid.
    /// </summary>
    [Fact]
    public void The_mind_does_not_read_the_seed_before_activation()
    {
        var seed = new GenesisSeedRuntime();
        seed.Load(new GenesisSeedData("calm-technical", new[] { "honesty" }, T0));

        Assert.Equal(SeedStatus.Seeded, seed.Status);
        Assert.Null(seed.Snapshot());

        seed.Activate();

        Assert.Equal(SeedStatus.Activated, seed.Status);
        Assert.NotNull(seed.Snapshot());
        Assert.Equal("calm-technical", seed.Snapshot()!.Temperament);
    }

    /// <summary>A seed cannot be re-loaded after activation — the identity is born once.</summary>
    [Fact]
    public void The_seed_cannot_be_reloaded_after_activation()
    {
        var seed = new GenesisSeedRuntime();
        seed.Load(new GenesisSeedData("calm-technical", new[] { "honesty" }, T0));
        seed.Activate();

        Assert.Throws<InvalidOperationException>(() =>
            seed.Load(new GenesisSeedData("other", new[] { "x" }, T0)));
    }

    // ---------------------------------------------------- 2.2 ceremony activates

    /// <summary>
    /// The whole point of the phase: after the ceremony the mind reports Activated. Before
    /// the fix this stayed NotSeeded forever because nothing called Activate().
    /// </summary>
    [Fact]
    public async Task The_ceremony_activates_the_seed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var (token, deviceId) = await EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var code = await EnrollMfaAndGetCodeAsync(client, deviceId);

        var seedResponse = await client.PostAsJsonAsync("/genesis/seed", new
        {
            temperament = "calm-technical-dry-humor",
            coreValues = new[] { "honesty", "non-destruction" },
            mfaDeviceId = deviceId,
            mfaCode = code
        });
        Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);
        Assert.Equal("Seeded", await MindSeedStatusAsync(client));

        var ceremony = await client.PostAsJsonAsync("/genesis/ceremony", new
        {
            codeIdentity = "22133e8",
            knowledgeIdentity = "knowledge-hash-test",
            mfaDeviceId = deviceId,
            mfaCode = code,
            backupConfirmed = true,
            restoreTested = true
        });
        Assert.Equal(HttpStatusCode.OK, ceremony.StatusCode);

        Assert.Equal("Activated", await MindSeedStatusAsync(client));
        Assert.NotNull(factory.Services.GetRequiredService<MindRuntime>().GenesisSeed.Snapshot());
    }

    // ------------------------------------------------ 2.3 MindState persistence

    /// <summary>
    /// 2.3 — a saved snapshot comes back through a NEW runtime, which is what a restart
    /// really is. The failure streak rides along because it is the mind's self-awareness
    /// input, not decoration.
    /// </summary>
    [Fact]
    public async Task Mind_state_survives_a_runtime_restart()
    {
        using var factory = new CoreHostFactory();
        var store = factory.Services.GetRequiredService<MindSnapshotStore>();

        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        mind.OnTaskFailed();
        mind.OnTaskFailed();
        mind.OnTaskFailed(); // streak = 3 → the mind should surface it

        var before = mind.Snapshot();
        Assert.Equal(3, mind.FailureStreak);
        Assert.True(before.Frustration > 0);

        await store.SaveAsync(mind.CaptureContinuity(), CancellationToken.None);

        // "Restart": a brand-new runtime that knows nothing.
        var reborn = new MindRuntime();
        var restored = await store.LoadLatestAsync(CancellationToken.None);
        Assert.NotNull(restored);
        reborn.Restore(restored!);

        var after = reborn.Snapshot();
        Assert.Equal(before.Frustration, after.Frustration, 6);
        Assert.Equal(before.Satisfaction, after.Satisfaction, 6);
        Assert.Equal(before.Boredom, after.Boredom, 6);
        Assert.Equal(before.Curiosity, after.Curiosity, 6);
        Assert.Equal(before.ActivityLevel, after.ActivityLevel);
        Assert.Equal(3, reborn.FailureStreak);
        Assert.True(reborn.ShouldSurfaceFailurePattern);
    }

    /// <summary>Saving twice keeps ONE row — the snapshot table is a singleton, not a log.</summary>
    [Fact]
    public async Task Mind_snapshot_store_keeps_a_single_row()
    {
        using var factory = new CoreHostFactory();
        var store = factory.Services.GetRequiredService<MindSnapshotStore>();

        var mind = new MindRuntime();
        mind.OnTaskFailed();
        await store.SaveAsync(mind.CaptureContinuity(), CancellationToken.None);

        mind.OnTaskFailed();
        await store.SaveAsync(mind.CaptureContinuity(), CancellationToken.None);

        await using var db = await factory.Services
            .GetRequiredService<IDbContextFactory<TolikDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.MindSnapshots.CountAsync());
    }

    /// <summary>With no saved row the store reports "nothing known" rather than inventing a state.</summary>
    [Fact]
    public async Task Mind_snapshot_store_returns_null_when_nothing_was_saved()
    {
        using var factory = new CoreHostFactory();
        var store = factory.Services.GetRequiredService<MindSnapshotStore>();

        Assert.Null(await store.LoadLatestAsync(CancellationToken.None));
    }

    // -------------------------------------------- 2.3b the seed must persist too

    /// <summary>
    /// The seed has the same continuity defect as MindState: held in a private field, so a
    /// restart would drop a BORN identity back to NotSeeded. The store must bring back both
    /// the data and the activation flag.
    /// </summary>
    [Fact]
    public async Task The_activated_seed_survives_a_restart()
    {
        using var factory = new CoreHostFactory();
        var store = factory.Services.GetRequiredService<GenesisSeedStore>();

        var seed = new GenesisSeedRuntime();
        seed.Load(new GenesisSeedData("calm-technical-dry-humor", new[] { "honesty", "non-destruction" }, T0));
        seed.Activate();

        await store.SaveAsync(seed, CancellationToken.None);

        var reborn = new GenesisSeedRuntime();
        var restored = await store.LoadLatestAsync(CancellationToken.None);
        Assert.NotNull(restored);
        Assert.True(restored!.Activated);

        store.Restore(reborn, restored);
        Assert.Equal(SeedStatus.Activated, reborn.Status);
        Assert.Equal("calm-technical-dry-humor", reborn.Snapshot()!.Temperament);
        Assert.Equal(2, reborn.Snapshot()!.CoreValues.Length);
    }
}
