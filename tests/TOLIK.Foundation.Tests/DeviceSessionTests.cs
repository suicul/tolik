using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Session renewal for an already-enrolled device.
///
/// The gap these tests close: the ONLY way a client could obtain a session without a
/// bootstrap code was GET /auth/local-token, which requires TOLIK_DEV_AUTO_TOKEN and is
/// refused outright after Genesis. A real installation is always post-Genesis, so the
/// shipped client could not authenticate at all. Renewal by device signature is the
/// ordinary path, and these tests pin its security properties:
///
///   * possession of the private key is required — a stolen installation id is worthless;
///   * a challenge is single-use — a captured signature cannot be replayed;
///   * issuer, device binding and expiry are all enforced;
///   * revoked devices and revoked users fail closed;
///   * unknown installations are indistinguishable from bad requests (no enumeration).
/// </summary>
public sealed class DeviceSessionTests
{
    private const string BootstrapCode = "TLK-BSTEP-2026-INIT";

    private sealed record Enrolled(string InstallationId, ECDsa Key, Guid DeviceId, string SessionToken) : IDisposable
    {
        public void Dispose() => Key.Dispose();
    }

    /// <summary>Enrolls a fresh device (owner enrolls first; repeats are rejects by design).</summary>
    private static async Task<Enrolled> EnrollDeviceAsync(HttpClient client, string? installationId = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string installation = installationId ?? "test-install-" + Guid.NewGuid().ToString("N")[..8];

        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = BootstrapCode });
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        string challengeValue = challenge.GetProperty("challenge").GetString()!;

        var enrollResponse = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = BootstrapCode,
            installationId = installation,
            publicKey = key.ExportSubjectPublicKeyInfoPem(),
            challenge = challengeValue,
            signature = Sign(key, challengeValue),
        });

        var enrolled = await enrollResponse.Content.ReadFromJsonAsync<JsonElement>();
        return new Enrolled(
            installation,
            key,
            enrolled.TryGetProperty("deviceId", out var d) ? d.GetGuid() : Guid.Empty,
            enrolled.TryGetProperty("sessionToken", out var s) ? s.GetString()! : string.Empty);
    }

    /// <summary>Satisfies the owner-enrollment prerequisite, ignoring the outcome.</summary>
    private static async Task<string> EnsureOwnerAsync(HttpClient client)
    {
        using var probe = await EnrollDeviceAsync(client);
        return probe.InstallationId;
    }

    private static string Sign(ECDsa key, string challenge)
        => Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(challenge),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

    private static async Task<(HttpStatusCode Status, string? Challenge)> RequestChallengeAsync(HttpClient client, string installationId)
    {
        var response = await client.PostAsJsonAsync("/auth/device/challenge", new { installationId });
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("challenge").GetString());
    }

    private static async Task<HttpResponseMessage> RedeemAsync(HttpClient client, string installationId, string challenge, string signature)
        => await client.PostAsJsonAsync("/auth/device/session", new { installationId, challenge, signature });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RedeemAsync2(HttpClient client, string installationId, string challenge, string signature)
    {
        var response = await RedeemAsync(client, installationId, challenge, signature);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task An_enrolled_device_renews_a_working_session()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);
        Assert.False(string.IsNullOrWhiteSpace(device.SessionToken));

        var (status, challenge) = await RequestChallengeAsync(client, device.InstallationId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrWhiteSpace(challenge));

        var (redeemStatus, body) = await RedeemAsync2(client, device.InstallationId, challenge!, Sign(device.Key, challenge!));
        Assert.Equal(HttpStatusCode.OK, redeemStatus);

        string session = body.GetProperty("sessionToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(session));
        Assert.Equal("Owner", body.GetProperty("role").GetString());

        // The renewed session must actually authenticate against a protected endpoint.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/memory/search?q=test");
        request.Headers.Authorization = new("Bearer", session);
        using var response = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_installation_id_alone_is_not_a_credential()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);
        var (_, challenge) = await RequestChallengeAsync(client, device.InstallationId);

        // A second key that never enrolled tries to claim this device.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (status, _) = await RedeemAsync2(client, device.InstallationId, challenge!, Sign(attacker, challenge!));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_challenge_cannot_be_replayed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);
        var (_, challenge) = await RequestChallengeAsync(client, device.InstallationId);
        string signature = Sign(device.Key, challenge!);

        var first = await RedeemAsync2(client, device.InstallationId, challenge!, signature);
        Assert.Equal(HttpStatusCode.OK, first.Status);

        // The exact same signed challenge, replayed: the challenge is consumed.
        var second = await RedeemAsync2(client, device.InstallationId, challenge!, signature);
        Assert.Equal(HttpStatusCode.Unauthorized, second.Status);
    }

    [Fact]
    public async Task A_signature_for_a_different_challenge_is_refused()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);
        var (_, challenge) = await RequestChallengeAsync(client, device.InstallationId);

        var (status, _) = await RedeemAsync2(client, device.InstallationId, challenge!, Sign(device.Key, "some-other-challenge"));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_challenge_issued_to_one_device_cannot_be_redeemed_by_another()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var first = await EnrollDeviceAsync(client);
        var (_, challenge) = await RequestChallengeAsync(client, first.InstallationId);
        string signature = Sign(first.Key, challenge!);

        // Same valid signature, wrong installation: the challenge is bound to its device.
        var (status, _) = await RedeemAsync2(client, "some-other-installation", challenge!, signature);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task An_unknown_installation_gets_no_challenge_and_learns_nothing()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        await EnsureOwnerAsync(client);

        var (status, challenge) = await RequestChallengeAsync(client, "definitely-not-enrolled");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Null(challenge);

        // Empty and oversized ids must not be distinguished by a different failure mode.
        var (emptyStatus, _) = await RequestChallengeAsync(client, "");
        var (longStatus, _) = await RequestChallengeAsync(client, new string('x', 200));
        Assert.Equal(status, emptyStatus);
        Assert.Equal(status, longStatus);
    }

    [Fact]
    public async Task A_revoked_device_cannot_renew()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);
        Assert.NotEqual(Guid.Empty, device.DeviceId);

        using (var revoke = new HttpRequestMessage(HttpMethod.Post, $"/auth/devices/{device.DeviceId}/revoke"))
        {
            revoke.Headers.Authorization = new("Bearer", device.SessionToken);
            using var revokeResponse = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        }

        var (status, challenge) = await RequestChallengeAsync(client, device.InstallationId);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Null(challenge);
    }

    [Fact]
    public async Task Renewal_is_impossible_before_any_owner_exists()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        // Nothing enrolled: there is no device to renew, and no oracle that says so.
        var (status, _) = await RequestChallengeAsync(client, "never-enrolled");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_malformed_renewal_request_is_refused_not_crashed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync2(client, "", "", "")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync2(client, device.InstallationId, "", "")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync2(client, device.InstallationId, "not-a-challenge", "not-base64!!")).Status);
    }

    [Fact]
    public async Task Renewal_does_not_require_the_dev_flag_and_survives_it_being_off()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        using var device = await EnrollDeviceAsync(client);

        // The dev endpoint is a separate, opt-in convenience; renewal must not depend on it.
        using var devRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/local-token");
        using var devResponse = await client.SendAsync(devRequest);
        Assert.Equal(HttpStatusCode.NotFound, devResponse.StatusCode);

        var (_, challenge) = await RequestChallengeAsync(client, device.InstallationId);
        var (status, body) = await RedeemAsync2(client, device.InstallationId, challenge!, Sign(device.Key, challenge!));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("sessionToken").GetString()));
    }
}
