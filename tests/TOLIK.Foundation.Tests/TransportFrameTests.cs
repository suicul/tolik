using System.Security.Cryptography;
using System.Text.Json;
using TOLIK.Infrastructure.Transport;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Signed transport frames (T9/T10, ported from interface/transport.js seal/open):
/// every rejection stage fails closed — MALFORMED_FRAME → AUDIENCE_MISMATCH →
/// FRAME_EXPIRED → DUPLICATE_PROPERTY/MALFORMED_JSON → UNKNOWN_SIGNER →
/// INVALID_SIGNATURE → REPLAY_DETECTED — and a valid frame consumes its nonce
/// exactly once. The pin key is the raw kid (README discrepancy sync decision).
/// </summary>
public sealed class TransportFrameTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);

    private static (ECDsa Key, string Pem) KeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportSubjectPublicKeyInfoPem());
    }

    private static string PayloadJson() => JsonSerializer.Serialize(new { correlationId = "c-1", messageType = "command_response", value = 42 });

    [Fact]
    public void Sealed_frame_opens_and_consumes_nonce_once()
    {
        var (key, pem) = KeyPair();
        var nonces = new NonceStore();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        var frame = SignedTransport.Seal(PayloadJson(), "core", "kid-1", key, "nonce-1", FixedNow);

        var result = SignedTransport.Open(frame, nonces, peers, FixedNow);
        Assert.True(result.Ok);
        Assert.Equal("kid-1", result.Frame!.SignerKid);
        // Replay: the same nonce can never be consumed again.
        var replay = SignedTransport.Open(frame, nonces, peers, FixedNow);
        Assert.False(replay.Ok);
        Assert.Equal("REPLAY_DETECTED", replay.ErrorCode);
    }

    [Fact]
    public void Tampered_payload_is_rejected_with_invalid_signature()
    {
        var (key, pem) = KeyPair();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        var frame = SignedTransport.Seal(PayloadJson(), "core", "kid-1", key, "nonce-2", FixedNow);
        var tampered = frame with { Payload = JsonSerializer.Serialize(new { correlationId = "c-1", messageType = "command_response", value = 43 }) };

        var result = SignedTransport.Open(tampered, new NonceStore(), peers, FixedNow);
        Assert.False(result.Ok);
        Assert.Equal("INVALID_SIGNATURE", result.ErrorCode);
    }

    [Fact]
    public void Unknown_signer_is_rejected()
    {
        var (key, _) = KeyPair();
        var peers = new PeerRegistry();
        var frame = SignedTransport.Seal(PayloadJson(), "core", "unknown-kid", key, "nonce-3", FixedNow);
        var result = SignedTransport.Open(frame, new NonceStore(), peers, FixedNow);
        Assert.False(result.Ok);
        Assert.Equal("UNKNOWN_SIGNER", result.ErrorCode);
    }

    [Fact]
    public void Wrong_audience_is_rejected()
    {
        var (key, pem) = KeyPair();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        var frame = SignedTransport.Seal(PayloadJson(), "agent", "kid-1", key, "nonce-4", FixedNow);
        var result = SignedTransport.Open(frame, new NonceStore(), peers, FixedNow, expectedAudience: "core");
        Assert.False(result.Ok);
        Assert.Equal("AUDIENCE_MISMATCH", result.ErrorCode);
    }

    [Fact]
    public void Expired_frame_is_rejected()
    {
        var (key, pem) = KeyPair();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        var frame = SignedTransport.Seal(PayloadJson(), "core", "kid-1", key, "nonce-5", FixedNow, TimeSpan.FromSeconds(1));
        var result = SignedTransport.Open(frame, new NonceStore(), peers, FixedNow.AddSeconds(2));
        Assert.False(result.Ok);
        Assert.Equal("FRAME_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public void Malformed_frames_fail_closed()
    {
        var peers = new PeerRegistry();
        peers.Pin("kid-1", "-----BEGIN PUBLIC KEY-----");
        // Null frame, missing fields, non-timestamp expiry.
        Assert.Equal("MALFORMED_FRAME", SignedTransport.Open(null, new NonceStore(), peers, FixedNow).ErrorCode);
        var empty = new SignedFrame(SignedTransport.FrameVersion, "core", "", "ES256", "n", "t", "t", "{}", "sig");
        Assert.Equal("MALFORMED_FRAME", SignedTransport.Open(empty, new NonceStore(), peers, FixedNow).ErrorCode);
        var badExpiry = new SignedFrame(SignedTransport.FrameVersion, "core", "kid-1", "ES256", "n", FixedNow.ToString("O"), "not-a-time", "{}", "sig");
        Assert.Equal("MALFORMED_FRAME", SignedTransport.Open(badExpiry, new NonceStore(), peers, FixedNow).ErrorCode);
    }

    [Fact]
    public void Malformed_and_duplicate_payloads_are_rejected()
    {
        var (key, pem) = KeyPair();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        var malformed = SignedTransport.Seal("{not json", "core", "kid-1", key, "nonce-6", FixedNow);
        Assert.Equal("MALFORMED_JSON", SignedTransport.Open(malformed, new NonceStore(), peers, FixedNow).ErrorCode);

        var duplicateRaw = """{"a":1,"a":2}""";
        var duplicate = SignedTransport.Seal(duplicateRaw, "core", "kid-1", key, "nonce-7", FixedNow);
        Assert.Equal("DUPLICATE_PROPERTY", SignedTransport.Open(duplicate, new NonceStore(), peers, FixedNow).ErrorCode);
    }

    [Fact]
    public void Signature_from_a_foreign_key_is_rejected()
    {
        var (realKey, pem) = KeyPair();
        var (foreignKey, _) = KeyPair();
        var peers = new PeerRegistry();
        peers.Pin("kid-1", pem);
        // Frame sealed with a foreign key but pinned to the real key's kid.
        var frame = SignedTransport.Seal(PayloadJson(), "core", "kid-1", foreignKey, "nonce-8", FixedNow);
        var result = SignedTransport.Open(frame, new NonceStore(), peers, FixedNow);
        Assert.False(result.Ok);
        Assert.Equal("INVALID_SIGNATURE", result.ErrorCode);
    }

    [Fact]
    public void Nonce_store_is_single_use_and_sweeps_expired_entries()
    {
        var nonces = new NonceStore();
        // Fresh entry at the fixed reference time: single-use until expiry.
        Assert.True(nonces.Consume("n-1", FixedNow, FixedNow.AddSeconds(60)));
        Assert.True(nonces.Has("n-1", FixedNow));
        Assert.False(nonces.Consume("n-1", FixedNow, FixedNow.AddSeconds(60)));
        // An already-expired entry is swept by the next call and no longer blocks.
        Assert.True(nonces.Consume("n-2", FixedNow, FixedNow.AddSeconds(-1)));
        Assert.False(nonces.Has("n-2", FixedNow.AddSeconds(1)));
        Assert.True(nonces.Consume("n-2", FixedNow, FixedNow.AddSeconds(60)));
        Assert.Equal(2, nonces.Count(FixedNow.AddSeconds(1)));
    }

    [Fact]
    public void Peer_registry_pins_by_raw_kid_and_unpins()
    {
        var peers = new PeerRegistry();
        peers.Pin("kid-1", "pem-1");
        Assert.Equal("pem-1", peers.Resolve("kid-1"));
        Assert.Null(peers.Resolve("agent:kid-1")); // raw kid, not prefixed
        Assert.True(peers.Unpin("kid-1"));
        Assert.Null(peers.Resolve("kid-1"));
    }
}
