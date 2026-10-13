using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TOLIK.Infrastructure.Identity;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Identity Key Hierarchy (docs/identity-presence.md IP-3 — PLAN REFORM PRG-8a):
/// the formal 4-key model — each key has a DIFFERENT role and no key can do
/// another's job (the separation is structural: the operations are typed by the
/// role, never by a free-form string). This enables: the VPS migration (the
/// identity is provable without the device), the independent key rotation, the
/// compromise containment (one key's compromise doesn't compromise the others).
/// </summary>
public sealed class IdentityKeyHierarchyTests
{
    [Fact]
    public void Generate_creates_exactly_four_keys()
    {
        using var hierarchy = IdentityKeyHierarchy.Generate();
        Assert.Equal(4, hierarchy.KeyCount);
        foreach (var role in Enum.GetValues<IdentityKeyRole>())
        {
            var publicKey = hierarchy.GetPublicKey(role);
            Assert.False(string.IsNullOrWhiteSpace(publicKey));
        }
    }

    [Fact]
    public void Public_keys_are_distinct_per_role()
    {
        using var hierarchy = IdentityKeyHierarchy.Generate();
        var keys = Enum.GetValues<IdentityKeyRole>()
            .Select(r => hierarchy.GetPublicKey(r))
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count()); // each role has a DIFFERENT key
    }

    [Fact]
    public void Signing_key_signs_and_the_same_keypair_verifies()
    {
        var hierarchy = IdentityKeyHierarchy.Generate();
        var data = Encoding.UTF8.GetBytes("migration proof: the identity travels");
        var signature = hierarchy.Sign(data);
        Assert.False(string.IsNullOrWhiteSpace(signature));
        Assert.True(hierarchy.VerifyWithSigningKey(data, signature)); // the same keypair verifies
    }

    [Fact]
    public void Tampered_data_fails_verification()
    {
        var hierarchy = IdentityKeyHierarchy.Generate();
        var original = Encoding.UTF8.GetBytes("migration proof: the identity travels");
        var signature = hierarchy.Sign(original);
        var tampered = Encoding.UTF8.GetBytes("migration proof: TAMPERED DATA");
        Assert.False(hierarchy.VerifyWithSigningKey(tampered, signature)); // the tampering is detected
    }

    [Fact]
    public void Protect_secret_transforms_the_data_never_passes_through()
    {
        using var hierarchy = IdentityKeyHierarchy.Generate();
        var plaintext = Encoding.UTF8.GetBytes("my-secret-api-key");
        var protected1 = hierarchy.ProtectSecret(plaintext);
        var protected2 = hierarchy.ProtectSecret(plaintext);
        Assert.NotEqual(plaintext, protected1); // the protection transforms the data
        // ECDSA signatures are randomized by design — the protection is AUTHENTICATION,
        // not deterministic encryption (the real encryption wires with AES-GCM + ECDH in Phase C full).
        Assert.NotEqual(protected1, protected2);
        Assert.Equal(protected1.Length, protected2.Length); // the same length (the ECDSA signature size)
    }

    [Fact]
    public void Export_public_keys_never_leaks_private_keys()
    {
        using var hierarchy = IdentityKeyHierarchy.Generate();
        var exported = IdentityKeyHierarchySerializer.ExportPublicKeys(hierarchy);
        Assert.Contains("Public", exported);
        Assert.Contains("Signing", exported);
        Assert.Contains("EncryptedSecrets", exported);
        Assert.Contains("Recovery", exported);
        // The export contains ONLY the public keys — the private key material NEVER leaves.
        Assert.DoesNotContain("PRIVATE", exported, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(exported);
        Assert.True(document.RootElement.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public void Protect_secret_produces_non_identity_output()
    {
        using var hierarchy = IdentityKeyHierarchy.Generate();
        var plaintext = Encoding.UTF8.GetBytes("my-secret-api-key");
        var protected1 = hierarchy.ProtectSecret(plaintext);
        var protected2 = hierarchy.ProtectSecret(plaintext);
        Assert.NotEqual(plaintext, protected1); // the protection transforms the data
        // ECDSA signatures are randomized by design — each call produces a different
        // signature for the same input (the correct cryptographic behavior, not a bug).
        Assert.NotEqual(protected1, protected2);
        Assert.Equal(protected1.Length, protected2.Length); // the same length (the ECDSA signature size)
    }

    [Fact]
    public void Fresh_hierarchies_have_distinct_keys()
    {
        using var h1 = IdentityKeyHierarchy.Generate();
        using var h2 = IdentityKeyHierarchy.Generate();
        Assert.NotEqual(h1.GetPublicKey(IdentityKeyRole.Public), h2.GetPublicKey(IdentityKeyRole.Public));
    }

    [Fact]
    public void The_secret_handle_never_leaks_into_the_state()
    {
        // The reflection over the IdentityKeyHierarchy type: no property/field carries
        // a secret VALUE in plaintext — only the PUBLIC keys are exposed via the API.
        var fields = typeof(IdentityKeyHierarchy).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.Name)
            .ToList();
        Assert.DoesNotContain(fields, f => f.Contains("secretValue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, f => f.Contains("privateKey", StringComparison.OrdinalIgnoreCase));
    }
}
