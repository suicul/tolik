using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TOLIK.Infrastructure.Transport;

/// <summary>
/// Ported from interface/transport.js NonceStore: replay protection for signed
/// transport frames (T10). A consumed nonce can never be consumed again; entries
/// expire with their frame TTL and are swept lazily. The reference time is injectable
/// per call so tests are deterministic (no dependence on the real clock).
/// </summary>
public sealed class NonceStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new();
    private readonly TimeSpan _ttl;

    public NonceStore(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(5);

    public int Size => Count(DateTimeOffset.UtcNow);

    public int Count(DateTimeOffset now) { Sweep(now); return _consumed.Count; }

    public bool Has(string nonce) => Has(nonce, DateTimeOffset.UtcNow);

    public bool Has(string nonce, DateTimeOffset now) { Sweep(now); return _consumed.ContainsKey(nonce); }

    /// <summary>
    /// Single-use consumption at the reference time `now`; succeeds only the first
    /// time a nonce is seen before its expiry.
    /// </summary>
    public bool Consume(string nonce, DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(nonce)) return false;
        Sweep(now);
        var expiry = expiresAt ?? now.Add(_ttl);
        return _consumed.TryAdd(nonce, expiry);
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (nonce, expiry) in _consumed)
            if (expiry <= now) _consumed.TryRemove(nonce, out _);
    }
}

/// <summary>
/// Pinned peer public keys by raw signer kid (sync decision from the README
/// discrepancy note, docs/INTERFACE-ANALYSIS.md: the C# side uses the raw kid as the
/// pin key — transport.js semantics — not an `agent:`-prefixed id).
/// </summary>
public sealed class PeerRegistry
{
    private readonly ConcurrentDictionary<string, string> _keys = new(StringComparer.Ordinal);

    public void Pin(string kid, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(publicKeyPem))
            throw new ArgumentException("kid and publicKeyPem are required.");
        _keys[kid] = publicKeyPem;
    }

    public bool Unpin(string kid) => _keys.TryRemove(kid, out _);

    public string? Resolve(string kid) => _keys.TryGetValue(kid, out var key) ? key : null;
}

/// <summary>
/// Signed transport frame (T9/T10, ported from interface/transport.js seal/open):
/// the payload is serialized once and digested into the header; the header is signed
/// with the sender's pinned key. Rejection chain — MALFORMED_FRAME → AUDIENCE_MISMATCH
/// → FRAME_EXPIRED → DUPLICATE_PROPERTY/MALFORMED_JSON → UNKNOWN_SIGNER →
/// INVALID_SIGNATURE → REPLAY_DETECTED — fails closed at every stage.
/// </summary>
public sealed record SignedFrame(
    string FrameVersion,
    string Audience,
    string SignerKid,
    string Algorithm,
    string Nonce,
    string IssuedAtUtc,
    string ExpiresAtUtc,
    string Payload,
    string Signature);

public sealed record FrameParseResult(bool Ok, string? ErrorCode, string? Detail, SignedFrame? Frame)
{
    public static FrameParseResult Fail(string code, string detail) => new(false, code, detail, null);
}

public static class SignedTransport
{
    public const string FrameVersion = "v1";
    public static readonly TimeSpan DefaultFrameTtl = TimeSpan.FromSeconds(30);

    private sealed record FrameHeader(
        string FrameVersion,
        string Audience,
        string SignerKid,
        string Algorithm,
        string Nonce,
        string IssuedAtUtc,
        string ExpiresAtUtc,
        string PayloadDigest);

    /// <summary>Seals a JSON payload into a signed frame (prototype seal).</summary>
    public static SignedFrame Seal(
        string payloadJson,
        string audience,
        string signerKid,
        ECDsa key,
        string nonce,
        DateTimeOffset now,
        TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) throw new ArgumentException("Payload is required.", nameof(payloadJson));
        if (string.IsNullOrWhiteSpace(audience)) throw new ArgumentException("Audience is required.", nameof(audience));
        if (string.IsNullOrWhiteSpace(signerKid)) throw new ArgumentException("Signer kid is required.", nameof(signerKid));
        if (string.IsNullOrWhiteSpace(nonce)) throw new ArgumentException("Nonce is required.", nameof(nonce));
        var expiresAt = now.Add(ttl ?? DefaultFrameTtl);
        var header = new FrameHeader(
            FrameVersion, audience, signerKid, "ES256", nonce,
            now.ToString("O"), expiresAt.ToString("O"), payloadJson);
        var headerJson = JsonSerializer.Serialize(header);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(headerJson), HashAlgorithmName.SHA256));
        return new SignedFrame(FrameVersion, audience, signerKid, "ES256", nonce,
            header.IssuedAtUtc, header.ExpiresAtUtc, payloadJson, signature);
    }

    /// <summary>
    /// Opens and verifies a frame (prototype open): every rejection stage fails closed;
    /// a valid frame consumes its nonce exactly once (replay protection).
    /// </summary>
    public static FrameParseResult Open(
        SignedFrame? frame,
        NonceStore nonces,
        PeerRegistry peers,
        DateTimeOffset now,
        string? expectedAudience = null)
    {
        if (frame is null) return FrameParseResult.Fail("MALFORMED_FRAME", "frame is not an object");
        if (string.IsNullOrWhiteSpace(frame.Audience) || string.IsNullOrWhiteSpace(frame.SignerKid) ||
            string.IsNullOrWhiteSpace(frame.Algorithm) || string.IsNullOrWhiteSpace(frame.Nonce) ||
            string.IsNullOrWhiteSpace(frame.IssuedAtUtc) || string.IsNullOrWhiteSpace(frame.ExpiresAtUtc) ||
            string.IsNullOrWhiteSpace(frame.Payload) || string.IsNullOrWhiteSpace(frame.Signature))
            return FrameParseResult.Fail("MALFORMED_FRAME", "frame field is missing or empty");
        if (expectedAudience is not null && !string.Equals(frame.Audience, expectedAudience, StringComparison.Ordinal))
            return FrameParseResult.Fail("AUDIENCE_MISMATCH", $"frame was addressed to '{frame.Audience}', not '{expectedAudience}'");
        if (!DateTimeOffset.TryParse(frame.ExpiresAtUtc, out var expiresAt))
            return FrameParseResult.Fail("MALFORMED_FRAME", "expiresAtUtc is not a valid timestamp");
        if (expiresAt <= now)
            return FrameParseResult.Fail("FRAME_EXPIRED", $"frame expired at {frame.ExpiresAtUtc}");
        // Strict payload parse (§22): duplicate properties and malformed JSON reject.
        // JsonDocument.Parse tolerates duplicate property names — the serializer option
        // (AllowDuplicateProperties=false, matching ContractJson strictness) rejects them.
        try
        {
            _ = JsonSerializer.Deserialize<JsonElement>(frame.Payload, new JsonSerializerOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return FrameParseResult.Fail("DUPLICATE_PROPERTY", ex.Message);
        }
        catch (JsonException ex)
        {
            return FrameParseResult.Fail("MALFORMED_JSON", ex.Message);
        }
        var pem = peers.Resolve(frame.SignerKid);
        if (pem is null)
            return FrameParseResult.Fail("UNKNOWN_SIGNER", $"no trusted public key is pinned for signer '{frame.SignerKid}'");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            var header = new FrameHeader(frame.FrameVersion, frame.Audience, frame.SignerKid,
                frame.Algorithm, frame.Nonce, frame.IssuedAtUtc, frame.ExpiresAtUtc, frame.Payload);
            var headerJson = JsonSerializer.Serialize(header);
            if (!key.VerifyData(Encoding.UTF8.GetBytes(headerJson), Convert.FromBase64String(frame.Signature), HashAlgorithmName.SHA256))
                return FrameParseResult.Fail("INVALID_SIGNATURE", "frame signature does not verify against the pinned peer key");
        }
        catch (FormatException)
        {
            return FrameParseResult.Fail("INVALID_SIGNATURE", "frame signature is not valid base64");
        }
        catch (CryptographicException)
        {
            return FrameParseResult.Fail("INVALID_SIGNATURE", "pinned key is not a valid ECDSA key");
        }
        if (!nonces.Consume(frame.Nonce, now, expiresAt))
            return FrameParseResult.Fail("REPLAY_DETECTED", $"nonce '{frame.Nonce}' was already consumed");
        return new FrameParseResult(true, null, null, frame);
    }
}
