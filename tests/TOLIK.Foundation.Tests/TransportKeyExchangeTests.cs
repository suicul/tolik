using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Transport;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Signed transport key exchange (stage d part 2, docs/INTERFACE-ANALYSIS.md): agents
/// present a P-256 public key at connect — Core pins it by the agent's raw id
/// (transport.js semantics); a malformed PEM fails closed (400). The WS hello carries
/// the core transport public key so agents can pin it. Legacy unsigned agents keep
/// working (additive fields, no frame contract change in part 2a).
/// </summary>
public sealed class TransportKeyExchangeTests
{
    private static string GoodPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    [Fact]
    public void Core_transport_keys_round_trip_sign_and_verify()
    {
        var keys = new CoreTransportKeys();
        Assert.Equal("core", CoreTransportKeys.Kid);
        Assert.False(string.IsNullOrWhiteSpace(keys.PublicKeyPem));
        var data = System.Text.Encoding.UTF8.GetBytes("""{"type":"command","step":1}""");
        var signature = keys.Sign(data);
        using var verify = ECDsa.Create();
        verify.ImportFromPem(keys.PublicKeyPem);
        Assert.True(verify.VerifyData(data, signature, HashAlgorithmName.SHA256));
        Assert.False(verify.VerifyData(System.Text.Encoding.UTF8.GetBytes("tampered"), signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Connect_with_valid_public_key_pins_and_succeeds()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var connect = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = Guid.NewGuid(),
            advertisedCapabilities = new[] { "readFile" },
            publicKey = GoodPem()
        });
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
    }

    [Fact]
    public async Task Connect_with_malformed_public_key_fails_closed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var connect = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = Guid.NewGuid(),
            advertisedCapabilities = new[] { "readFile" },
            publicKey = "not-a-valid-pem"
        });
        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
        Assert.Equal("invalid_public_key", (await connect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // Legacy unsigned agents still connect (additive key exchange, no breakage).
        var legacy = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = Guid.NewGuid(),
            advertisedCapabilities = new[] { "readFile" }
        });
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    [Fact]
    public async Task Ws_hello_carries_the_core_transport_public_key()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // The WS endpoint requires the agent id to be registered first (owned, fail closed).
        var agentId = Guid.NewGuid();
        var connect = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId,
            advertisedCapabilities = new[] { "readFile" },
            publicKey = GoodPem()
        });
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        // TestServer hosts are in-memory (no real socket) — the server's WebSocketClient
        // bridges the in-memory pipeline (raw ClientWebSocket would hit a real TCP port).
        var wsClient = factory.Server.CreateWebSocketClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsUri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/agents/ws",
            Query = $"token={Uri.EscapeDataString(sessionToken)}&agentId={agentId}"
        }.Uri;
        using var ws = await wsClient.ConnectAsync(wsUri, connectCts.Token);

        var buffer = new byte[8192];
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);

        var hello = JsonSerializer.Deserialize<JsonElement>(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count));
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal("core", hello.GetProperty("coreKid").GetString());
        var corePem = hello.GetProperty("corePublicKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(corePem));
        // The pinned key verifies a signature made by the core transport keys.
        var keys = factory.Services.GetRequiredService<CoreTransportKeys>();
        using var verify = ECDsa.Create();
        verify.ImportFromPem(corePem!);
        var data = System.Text.Encoding.UTF8.GetBytes("probe");
        Assert.True(verify.VerifyData(data, keys.Sign(data), HashAlgorithmName.SHA256));
    }
}
