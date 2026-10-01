using System.Security.Cryptography;
using System.Text;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

public sealed class IdentityEvidenceTests
{
    [Fact]
    public void Device_challenge_accepts_valid_signature_and_rejects_tampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = DeviceChallengeVerifier.CreateChallenge();
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256));
        var publicKey = key.ExportSubjectPublicKeyInfoPem();

        Assert.True(DeviceChallengeVerifier.Verify(challenge, publicKey, signature));
        Assert.False(DeviceChallengeVerifier.Verify(challenge + "tampered", publicKey, signature));
    }

    [Fact]
    public void Device_challenge_accepts_interoperable_der_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = DeviceChallengeVerifier.CreateChallenge();
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.True(DeviceChallengeVerifier.Verify(challenge, key.ExportSubjectPublicKeyInfoPem(), signature));
    }

    [Fact]
    public void Device_challenge_rejects_non_p256_keys_and_malformed_inputs()
    {
        using var rsa = RSA.Create(2048);
        var challenge = DeviceChallengeVerifier.CreateChallenge();
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(DeviceChallengeVerifier.Verify(challenge, rsa.ExportSubjectPublicKeyInfoPem(), signature));
        Assert.False(DeviceChallengeVerifier.Verify(challenge, "not-a-key", "%%%"));
    }

    [Fact]
    public void Device_challenge_accepts_external_style_p256_subject_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(DeviceChallengeVerifier.IsP256PublicKey(publicKey));
    }

    [Fact]
    public void Speaker_confidence_does_not_change_authenticated_authorization()
    {
        var user = Guid.NewGuid();
        Assert.True(IdentityAuthorization.IsAuthorized(user, user, new SpeakerEvidence("Ivan", 0.97, "test", DateTimeOffset.UtcNow)));
        Assert.True(IdentityAuthorization.IsAuthorized(user, user, new SpeakerEvidence("Ivan", 0.10, "test", DateTimeOffset.UtcNow)));
    }
}
