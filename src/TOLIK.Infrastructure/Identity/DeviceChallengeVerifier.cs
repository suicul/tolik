using System.Security.Cryptography;
using System.Text;

namespace TOLIK.Infrastructure.Identity;

public static class DeviceChallengeVerifier
{
    public static string CreateChallenge() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool Verify(string challenge, string publicKeyPem, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(signatureBase64))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(signatureBase64);
            var data = Encoding.UTF8.GetBytes(challenge);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                || key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsP256PublicKey(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var parameters = key.ExportParameters(false);
            return parameters.Q.X is { Length: 32 } && parameters.Q.Y is { Length: 32 } && parameters.Curve.Oid.Value == "1.2.840.10045.3.1.7";
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
