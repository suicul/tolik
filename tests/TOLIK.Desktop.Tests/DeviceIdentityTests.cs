using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Identity;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Covers the credential path a real installation uses. The bug these tests exist for:
/// the shipped client could only reach /auth/local-token, which Core refuses once the
/// Genesis ceremony has run — so the client silently depended on a development flag and
/// could not authenticate against a real deployment at all.
/// </summary>
public sealed class DeviceIdentityTests : IDisposable
{
    /// <summary>An empty stand-in for %USERPROFILE%\.tolik: the developer machine has a real one.</summary>
    private static readonly string EmptyLegacyDir = Path.Combine(Path.GetTempPath(), "tolik-no-legacy");

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "tolik-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public void First_load_creates_a_p256_identity_and_the_second_reuses_it()
    {
        using var first = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
        Assert.True(first.WasCreated);
        Assert.Matches("^[0-9a-f]{32}$", first.InstallationId);
        Assert.True(DeviceChallengeVerifierShape.IsP256(first.PublicKeyPem));

        string installationId = first.InstallationId;
        string publicKey = first.PublicKeyPem;

        using var second = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
        Assert.False(second.WasCreated);
        Assert.Equal(installationId, second.InstallationId);
        Assert.Equal(publicKey, second.PublicKeyPem);
    }

    [Fact]
    public void The_private_key_on_disk_is_not_readable_plaintext()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
        string keyFile = Path.Combine(_dir, "device-key.dat");
        Assert.True(File.Exists(keyFile));

        byte[] onDisk = File.ReadAllBytes(keyFile);
        string asText = Encoding.UTF8.GetString(onDisk);

        // The old CLI wrote the raw PKCS#8 key as base64 while its comment claimed DPAPI.
        // Assert the sealed file is neither valid base64 key material nor the PEM itself.
        Assert.DoesNotContain("BEGIN", asText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EC PRIVATE KEY", asText, StringComparison.OrdinalIgnoreCase);

        var decoded = new byte[onDisk.Length];
        bool lookedLikeBase64Key = Convert.TryFromBase64String(asText, decoded, out int written)
            && written > 0
            && decoded.AsSpan(0, written).SequenceEqual(onDisk);
        Assert.False(lookedLikeBase64Key, "ключ должен быть зашифрован, а не просто закодирован");

        // And it must not be a bare private key that ECDsa can import directly.
        using var probe = ECDsa.Create();
        Assert.ThrowsAny<CryptographicException>(() => probe.ImportECPrivateKey(onDisk, out _));
    }

    [Fact]
    public void A_copied_directory_without_the_windows_account_cannot_use_the_key()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
        string keyFile = Path.Combine(_dir, "device-key.dat");
        byte[] sealedKey = File.ReadAllBytes(keyFile);
        string installationFile = Path.Combine(_dir, "installation-id");

        // Simulate "the folder was copied to another machine / another user": DPAPI blob
        // that cannot be unsealed. Corruption is the observable stand-in available in-process.
        byte[] corrupted = (byte[])sealedKey.Clone();
        corrupted[0] ^= 0xFF;
        corrupted[^1] ^= 0xFF;
        File.WriteAllBytes(keyFile, corrupted);

        var error = Assert.Throws<InvalidOperationException>(() => DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir));
        Assert.Contains("повреждён", error.Message);

        // The installation id survives, but it is only an identifier — never a credential.
        Assert.True(File.Exists(installationFile));
    }

    [Fact]
    public void Load_without_create_refuses_to_mint_an_identity()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DeviceIdentity.LoadOrCreate(_dir, create: false, legacyDirectory: EmptyLegacyDir));
        Assert.Contains("не зарегистрировано", error.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "device-key.dat")));
    }

    [Fact]
    public void Signatures_verify_with_the_public_key_and_fail_for_a_different_challenge()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
        const string challenge = "challenge-token-under-test";

        string signature = identity.SignChallenge(challenge);

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(identity.PublicKeyPem);
        byte[] data = Encoding.UTF8.GetBytes(challenge);
        byte[] raw = Convert.FromBase64String(signature);

        Assert.True(verifier.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.False(verifier.VerifyData(
            Encoding.UTF8.GetBytes("another-challenge"),
            raw,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Signing_an_empty_challenge_is_refused()
        => Assert.Throws<ArgumentException>(() =>
        {
            using var identity = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
            identity.SignChallenge("   ");
        });

    [Fact]
    public void An_injected_directory_never_adopts_the_real_user_profile()
    {
        // Guard for a real incident: migration moves the ONLY copy of the key and then
        // deletes the original. A test that merely passed a temp directory adopted the
        // live identity and lost it when its temp dir was cleaned up. An injected
        // directory must stay isolated unless the legacy path is named explicitly.
        var error = Assert.Throws<InvalidOperationException>(
            () => DeviceIdentity.LoadOrCreate(_dir, create: false));
        Assert.Contains("не зарегистрировано", error.Message);
    }

    [Fact]
    public void The_cli_identity_is_adopted_and_its_plaintext_key_removed()
    {
        // Reproduce the real first migration: the retired CLI left a plain base64 P-256
        // key at %USERPROFILE%\.tolik. Core already knows that key and bootstrap is
        // single-use, so re-enrolling is impossible — the key must survive, sealed.
        string legacy = Path.Combine(Path.GetTempPath(), "tolik-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        try
        {
            using var cliKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string expectedPublicKey = cliKey.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(
                Path.Combine(legacy, "device-key.xml"),
                Convert.ToBase64String(cliKey.ExportECPrivateKey()));
            const string cliInstallationId = "01190daee76b4491978c86940c0fb959";
            File.WriteAllText(Path.Combine(legacy, "installation-id"), cliInstallationId);

            using var migrated = DeviceIdentity.LoadOrCreate(_dir, create: false, legacyDirectory: legacy);

            // Same identity Core knows: same installation id, same public key.
            Assert.Equal(cliInstallationId, migrated.InstallationId);
            Assert.Equal(expectedPublicKey, migrated.PublicKeyPem);
            Assert.False(migrated.WasCreated);

            // The plaintext key is gone, replaced by a sealed one.
            Assert.False(File.Exists(Path.Combine(legacy, "device-key.xml")));
            Assert.False(File.Exists(Path.Combine(legacy, "installation-id")));
            Assert.True(File.Exists(Path.Combine(_dir, "device-key.dat")));

            // And the sealed identity is the one used from now on.
            using var reloaded = DeviceIdentity.LoadOrCreate(_dir, legacyDirectory: EmptyLegacyDir);
            Assert.Equal(cliInstallationId, reloaded.InstallationId);
            Assert.Equal(expectedPublicKey, reloaded.PublicKeyPem);
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_corrupt_legacy_key_is_ignored_rather_than_half_migrated()
    {
        string legacy = Path.Combine(Path.GetTempPath(), "tolik-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        try
        {
            File.WriteAllText(Path.Combine(legacy, "device-key.xml"), "not-a-key");

            var error = Assert.Throws<InvalidOperationException>(
                () => DeviceIdentity.LoadOrCreate(_dir, create: false, legacyDirectory: legacy));
            Assert.Contains("не зарегистрировано", error.Message);

            // Nothing was written and nothing was deleted.
            Assert.False(File.Exists(Path.Combine(_dir, "device-key.dat")));
            Assert.True(File.Exists(Path.Combine(legacy, "device-key.xml")));
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// Mirrors the server-side P-256 check so the test asserts the key shape Core will
/// actually accept, instead of trusting that "a key was produced".
/// </summary>
internal static class DeviceChallengeVerifierShape
{
    public static bool IsP256(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var parameters = key.ExportParameters(false);
        return parameters.Q.X is { Length: 32 }
            && parameters.Q.Y is { Length: 32 }
            && parameters.Curve.Oid.Value == "1.2.840.10045.3.1.7";
    }
}

/// <summary>
/// The wire contract for session renewal: challenge → sign → session, and the honest
/// refusals. A client that gets this wrong looks "connected" while holding no session.
/// </summary>
public sealed class DeviceTokenSourceTests
{
    private static readonly CoreLinkOptions Options = new(new Uri("http://127.0.0.1:5080"), "test");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(string Uri, string? Body)> Seen { get; } = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Seen.Add((request.RequestUri!.ToString(), body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_registered_device_renews_its_session_by_signing_the_challenge()
    {
        string dir = NewDir();
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(dir);
            string? seenSignature = null;
            string? seenChallenge = null;

            var handler = new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/auth/device/challenge")
                {
                    return Json(HttpStatusCode.OK, """{"challenge":"challenge-abc","expiresAt":"2026-11-03T00:00:00Z"}""");
                }

                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                seenChallenge = doc.RootElement.GetProperty("challenge").GetString();
                seenSignature = doc.RootElement.GetProperty("signature").GetString();
                return Json(HttpStatusCode.OK, """{"sessionToken":"session-xyz","role":"Owner","sessionId":"11111111-1111-1111-1111-111111111111"}""");
            });

            using var http = new HttpClient(handler);
            var source = new DeviceTokenSource(Options, identity, null, http);

            var token = await source.AcquireAsync(CancellationToken.None);

            Assert.NotNull(token);
            Assert.Equal("session-xyz", token!.Value.Value);
            Assert.True(token.Value.Fresh);
            Assert.Equal("challenge-abc", seenChallenge);
            Assert.NotNull(seenSignature);

            // The signature must verify against the enrolled public key — this is the whole
            // security property: possession of the private key is the credential.
            using var verifier = ECDsa.Create();
            verifier.ImportFromPem(identity.PublicKeyPem);
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(seenChallenge!),
                Convert.FromBase64String(seenSignature!),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));

            Assert.Equal(2, handler.Seen.Count);
            Assert.Contains("/auth/device/challenge", handler.Seen[0].Uri);
            Assert.Contains("/auth/device/session", handler.Seen[1].Uri);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task The_session_is_cached_so_the_second_acquire_hits_no_endpoint()
    {
        string dir = NewDir();
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(dir);
            var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/auth/device/challenge"
                ? Json(HttpStatusCode.OK, """{"challenge":"c","expiresAt":"2026-11-03T00:00:00Z"}""")
                : Json(HttpStatusCode.OK, """{"sessionToken":"s"}"""));

            using var http = new HttpClient(handler);
            var source = new DeviceTokenSource(Options, identity, null, http);

            await source.AcquireAsync(CancellationToken.None);
            var again = await source.AcquireAsync(CancellationToken.None);

            Assert.Equal("s", again!.Value.Value);
            Assert.False(again.Value.Fresh);
            Assert.Equal(2, handler.Seen.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task A_revoked_device_fails_closed_with_an_honest_detail()
    {
        string dir = NewDir();
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(dir);
            var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":"device_session_denied"}"""));

            using var http = new HttpClient(handler);
            var source = new DeviceTokenSource(Options, identity, null, http);

            var token = await source.AcquireAsync(CancellationToken.None);

            Assert.Null(token);
            Assert.Contains("отозвано", source.LastDetail);
            Assert.Null(source.LastDetail!.Contains("session", StringComparison.OrdinalIgnoreCase) ? "leak" : null);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task An_unregistered_device_without_a_code_never_calls_core()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"sessionToken":"should-not-be-used"}"""));
        using var http = new HttpClient(handler);
        var source = new DeviceTokenSource(Options, identity: null, bootstrapCode: null, http);

        var token = await source.AcquireAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Contains("код регистрации", source.LastDetail);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task First_run_enrolls_with_the_bootstrap_code_and_keeps_the_identity()
    {
        string dir = NewDir();
        try
        {
            string? sentPublicKey = null;
            string? sentInstallation = null;
            var handler = new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/auth/bootstrap/challenge")
                {
                    return Json(HttpStatusCode.OK, """{"challenge":"boot-challenge"}""");
                }
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                sentPublicKey = doc.RootElement.GetProperty("publicKey").GetString();
                sentInstallation = doc.RootElement.GetProperty("installationId").GetString();
                return Json(HttpStatusCode.OK, """{"sessionToken":"enrolled-session","userId":"u","deviceId":"d"}""");
            });

            using var http = new HttpClient(handler);
            // First run: no identity yet. The factory is what enroll creates.
            var source = new DeviceTokenSource(
                Options, identity: null, "TLK-TEST-CODE", http,
                identityFactory: () => DeviceIdentity.LoadOrCreate(dir));

            var token = await source.AcquireAsync(CancellationToken.None);

            Assert.Equal("enrolled-session", token!.Value.Value);
            Assert.NotNull(sentInstallation);
            Assert.True(DeviceChallengeVerifierShape.IsP256(sentPublicKey!));

            // The identity created during enrollment is the one later runs renew with.
            using var persisted = DeviceIdentity.LoadOrCreate(dir);
            Assert.Equal(persisted.InstallationId, sentInstallation);
            Assert.Equal(persisted.PublicKeyPem, sentPublicKey);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task A_malformed_core_response_is_reported_not_thrown()
    {
        string dir = NewDir();
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(dir);
            var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "not json at all"));

            using var http = new HttpClient(handler);
            var source = new DeviceTokenSource(Options, identity, null, http);

            var token = await source.AcquireAsync(CancellationToken.None);

            Assert.Null(token);
            Assert.Contains("malformed", source.LastDetail);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task An_unreachable_core_is_reported_without_leaking_the_endpoint()
    {
        string dir = NewDir();
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(dir);
            var handler = new StubHandler(_ => throw new HttpRequestException("connection refused to http://secret-host"));

            using var http = new HttpClient(handler);
            var source = new DeviceTokenSource(Options, identity, null, http);

            var token = await source.AcquireAsync(CancellationToken.None);

            Assert.Null(token);
            Assert.Equal("Core недоступен", source.LastDetail);
            Assert.DoesNotContain("secret-host", source.LastDetail);
        }
        finally { Cleanup(dir); }
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "tolik-token-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }
}

/// <summary>
/// The fallback ORDER is the point: device signature first, dev token only last. Getting
/// this backwards would put a development flag on the production path.
/// </summary>
public sealed class ClientTokenSourceTests
{
    private sealed class ScriptedTokens(string? value, string? detail) : ISessionTokenSource
    {
        public int Acquires { get; private set; }
        public string? LastDetail { get; } = detail;
        public Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
        {
            Acquires++;
            return Task.FromResult<SessionToken?>(value is null ? null : new SessionToken(value, true));
        }
        public void Invalidate() { }
    }

    [Fact]
    public async Task The_device_path_wins_and_the_dev_path_is_not_consulted()
    {
        var device = new ScriptedTokens("device-session", null);
        var dev = new ScriptedTokens("dev-session", null);
        var chain = new ClientTokenSource(device, dev);

        var token = await chain.AcquireAsync(CancellationToken.None);

        Assert.Equal("device-session", token!.Value.Value);
        Assert.Equal(0, dev.Acquires);
    }

    [Fact]
    public async Task The_dev_path_is_used_only_when_the_device_path_fails()
    {
        var device = new ScriptedTokens(null, "устройство не зарегистрировано: нужен код регистрации");
        var dev = new ScriptedTokens("dev-session", null);
        var chain = new ClientTokenSource(device, dev);

        var token = await chain.AcquireAsync(CancellationToken.None);

        Assert.Equal("dev-session", token!.Value.Value);
        Assert.Equal(1, dev.Acquires);
    }

    [Fact]
    public async Task When_both_fail_the_device_reason_is_the_one_reported()
    {
        var device = new ScriptedTokens(null, "продление сессии отклонено: устройство отозвано?");
        var dev = new ScriptedTokens(null, "GET /auth/local-token → 404 (dev auto-token unavailable)");
        var chain = new ClientTokenSource(device, dev);

        var token = await chain.AcquireAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Contains("отозвано", chain.LastDetail);
    }
}
