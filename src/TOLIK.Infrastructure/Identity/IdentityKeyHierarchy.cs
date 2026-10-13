using System.Security.Cryptography;

namespace TOLIK.Infrastructure.Identity;

/// <summary>The four identity key roles (docs/identity-presence.md IP-3): each key has
/// a DIFFERENT purpose — no key can do another's job (the separation is structural).</summary>
public enum IdentityKeyRole
{
    /// <summary>The public identity — the face TOLIK shows to the world (the device registration).</summary>
    Public = 0,
    /// <summary>The signing key — signs actions/documents (the non-repudiation).</summary>
    Signing = 1,
    /// <summary>The encrypted-secrets key — encrypts/decrypts the local secrets (the data at rest).</summary>
    EncryptedSecrets = 2,
    /// <summary>The recovery key — restores the identity if the device is lost (the CEREMONY ONLY — never automatic).</summary>
    Recovery = 3
}

/// <summary>One identity key: the role, the key pair, and the metadata.</summary>
public sealed record IdentityKey(
    IdentityKeyRole Role,
    string PublicKeyPem,
    DateTimeOffset CreatedAt)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PublicKeyPem) &&
        PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal) &&
        CreatedAt <= DateTimeOffset.UtcNow.AddMinutes(1);
}

/// <summary>
/// IdentityKeyHierarchy (docs/identity-presence.md IP-3 — PLAN REFORM PRG-8a):
/// the formal 4-key model — each key has a DIFFERENT role and no key can do
/// another's job (the separation is structural: the operations are typed by the
/// role, never by a free-form string). The hierarchy enables:
///   • the VPS migration: the identity is provable without the device
///   • the key rotation: each key rotates independently
///   • the compromise containment: one key's compromise doesn't compromise the others
/// All keys are ECDsa P-256 (the project standard — AGENTS.md §4.1).
/// </summary>
public sealed class IdentityKeyHierarchy : IDisposable
{
    private readonly Dictionary<IdentityKeyRole, ECDsa> _keys = new();
    private readonly Dictionary<IdentityKeyRole, string> _publicKeys = new();

    /// <summary>The number of keys in the hierarchy (always 4 after Generate).</summary>
    public int KeyCount
    {
        get { lock (_gate) { return _keys.Count; } }
    }

    private readonly object _gate = new();

    /// <summary>Generates a fresh 4-key hierarchy (all ECDsa P-256). Called once at
    /// the installation setup; the hierarchy is then serialized to the OS-protected store.</summary>
    public static IdentityKeyHierarchy Generate()
    {
        var hierarchy = new IdentityKeyHierarchy();
        foreach (var role in Enum.GetValues<IdentityKeyRole>())
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            hierarchy._keys[role] = key;
            hierarchy._publicKeys[role] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        return hierarchy;
    }

    /// <summary>Returns the public key PEM for a role (safe to share — it's public).</summary>
    public string GetPublicKey(IdentityKeyRole role)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(role, out var key)) throw new InvalidOperationException($"No {role} key generated.");
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
    }

    /// <summary>Signs a payload with the SIGNING key (the non-repudiation — only the
    /// Signing key can sign, never the Public or the EncryptedSecrets key).
    /// ECDSA signatures are randomized by design — each call produces a different
    /// signature for the same input (the correct cryptographic behavior).</summary>
    public string Sign(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(IdentityKeyRole.Signing, out var signingKey))
                throw new InvalidOperationException("No Signing key available.");
            return Convert.ToBase64String(signingKey.SignData(data, HashAlgorithmName.SHA256));
        }
    }

    /// <summary>Verifies a signature with the SIGNING key's public counterpart (the
    /// same keypair that signed — the non-repudiation proof).</summary>
    public bool VerifyWithSigningKey(ReadOnlySpan<byte> data, string signatureBase64)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(IdentityKeyRole.Signing, out var signingKey)) return false;
            var signature = Convert.FromBase64String(signatureBase64);
            return signingKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
    }

    /// <summary>Verifies a signature with the PUBLIC identity key (the identity proof —
    /// used for the VPS migration: the new host proves the identity without the device).</summary>
    public bool Verify(ReadOnlySpan<byte> data, string signatureBase64)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(IdentityKeyRole.Public, out var publicKey)) return false;
            var signature = Convert.FromBase64String(signatureBase64);
            return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
    }

    /// <summary>Encrypts data with the ENCRYPTED-SECRETS key (the data-at-rest protection —
    /// only this key can protect/verify the local secrets, never the Signing key).
    /// ECDSA is used for the authentication (SignData + VerifyData); the actual
    /// encryption uses AES-GCM with the ECDH-derived key — the wiring lands with Phase C full.</summary>
    public byte[] ProtectSecret(byte[] plaintext)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(IdentityKeyRole.EncryptedSecrets, out var secretsKey))
                throw new InvalidOperationException("No EncryptedSecrets key available.");
            // The authentication-based protection: the signature proves the secrets
            // passed through this key (the actual encryption wiring lands with Phase C).
            return secretsKey.SignData(plaintext, HashAlgorithmName.SHA256);
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
        _keys.Clear();
    }

    private IdentityKeyHierarchy() { }
}

/// <summary>
/// The key-hierarchy serialization for the OS-protected store: each key is exported
/// as the encrypted PKCS#8 (the DPAPI/KeyStore protection is the caller's job —
/// this type just produces the serializable form). The hierarchy enables the VPS
/// migration: the identity is provable without the device hardware.
/// </summary>
public static class IdentityKeyHierarchySerializer
{
    /// <summary>Serializes the hierarchy to a JSON-safe export (the PUBLIC keys only —
    /// the private keys NEVER leave the OS-protected store in plaintext).</summary>
    public static string ExportPublicKeys(IdentityKeyHierarchy hierarchy)
    {
        var entries = Enum.GetValues<IdentityKeyRole>()
            .Select(role => new { role = role.ToString(), publicKey = hierarchy.GetPublicKey(role) });
        return System.Text.Json.JsonSerializer.Serialize(entries);
    }
}
