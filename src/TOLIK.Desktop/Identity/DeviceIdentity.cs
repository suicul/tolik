using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TOLIK.Desktop.Identity;

/// <summary>
/// The installation's cryptographic identity: a P-256 key that proves this machine is
/// the device TOLIK was enrolled on.
///
/// The private key is sealed with DPAPI under the CURRENT USER, so the file alone is
/// worthless on another machine or under another account (SECURITY §6/§11: copying the
/// application directory must not clone the identity). The CLI stored the same key as
/// plain base64 while its comment claimed DPAPI protection — that comment was false,
/// and the key was readable by anything running as the user.
///
/// The installation id is NOT trusted as a credential; it only tells Core which device
/// is asking. The signature is what authenticates.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private const string KeyFileName = "device-key.dat";
    private const string InstallationFileName = "installation-id";

    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key, string installationId, bool created)
    {
        _key = key;
        InstallationId = installationId;
        WasCreated = created;
    }

    /// <summary>Stable id for this installation. Not a secret — see the class remarks.</summary>
    public string InstallationId { get; }

    /// <summary>True when this call generated a brand-new key (first run on this machine).</summary>
    public bool WasCreated { get; }

    /// <summary>The public half, as the PEM Core enrolls and verifies against.</summary>
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>
    /// Loads the identity, creating it on first run. A corrupt or foreign-user key file is
    /// NOT silently replaced: regenerating the key would break the enrollment Core holds,
    /// and pretending it never happened would leave the user with an unexplainable
    /// rejection. The caller decides how to surface this.
    /// </summary>
    public static DeviceIdentity LoadOrCreate(string? directory = null, bool create = true, string? legacyDirectory = null)
    {
        string root = directory ?? DefaultDirectory();
        Directory.CreateDirectory(root);
        string keyPath = Path.Combine(root, KeyFileName);
        string installationPath = Path.Combine(root, InstallationFileName);

        if (File.Exists(keyPath))
        {
            byte[] sealedKey = File.ReadAllBytes(keyPath);
            byte[] pem = Unseal(sealedKey);
            var existing = ECDsa.Create();
            try
            {
                existing.ImportECPrivateKey(pem, out _);
            }
            catch (CryptographicException)
            {
                existing.Dispose();
                throw new InvalidOperationException(
                    "Файл ключа устройства повреждён или создан другим пользователем Windows.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }

            string loadedId = File.Exists(installationPath)
                ? File.ReadAllText(installationPath).Trim()
                : NewInstallationId();
            File.WriteAllText(installationPath, loadedId);
            return new DeviceIdentity(existing, loadedId, created: false);
        }

        // An installation enrolled by the retired CLI keeps its identity in
        // %USERPROFILE%\.tolik as a PLAIN base64 private key. Core already knows that
        // key, and bootstrap is single-use, so re-enrolling is impossible — the key must
        // be carried over. Adopting it also removes the plaintext secret: one owner for
        // the identity, sealed.
        //
        // Migration moves the ONLY copy of the key and then deletes the original, so it
        // is gated: a caller that supplies its own directory or legacy location is
        // isolated from the real profile. Without this gate a test that merely passed a
        // temp directory adopted the live identity, deleted the original and then lost it
        // with its own temp dir — the installation's key was destroyed that way once.
        bool productionPath = directory is null && legacyDirectory is null;
        bool legacyPathExplicit = legacyDirectory is not null;
        if (productionPath || legacyPathExplicit)
        {
            if (TryMigrateLegacy(legacyDirectory, keyPath, installationPath, out DeviceIdentity? migrated))
            {
                return migrated!;
            }
        }

        if (!create)
        {
            throw new InvalidOperationException("Устройство ещё не зарегистрировано на этом компьютере.");
        }

        var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] raw = fresh.ExportECPrivateKey();
        try
        {
            File.WriteAllBytes(keyPath, Seal(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        string id = NewInstallationId();
        File.WriteAllText(installationPath, id);
        return new DeviceIdentity(fresh, id, created: true);
    }

    /// <summary>
    /// Adopts the retired CLI's identity: same key, same installation id, sealed with
    /// DPAPI. The plaintext originals are removed only after the sealed copy is on disk
    /// and re-readable — a half-migrated identity would strand the installation.
    /// </summary>
    private static bool TryMigrateLegacy(string? legacyDirectory, string keyPath, string installationPath, out DeviceIdentity? identity)
    {
        identity = null;
        string legacy = legacyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tolik");
        string legacyKey = Path.Combine(legacy, "device-key.xml");
        string legacyInstallation = Path.Combine(legacy, "installation-id");

        if (!File.Exists(legacyKey))
        {
            return false;
        }

        string base64 = File.ReadAllText(legacyKey).Trim();
        if (base64.Length == 0)
        {
            return false;
        }

        byte[] legacyRaw;
        try
        {
            legacyRaw = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // Unparseable legacy file: treat it as absent rather than crash the client.
            return false;
        }

        var adopted = ECDsa.Create();
        try
        {
            adopted.ImportECPrivateKey(legacyRaw, out _);
        }
        catch (CryptographicException)
        {
            adopted.Dispose();
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyRaw);
        }

        // Keep the SAME installation id, otherwise Core sees an unknown device.
        string installationId = File.Exists(legacyInstallation)
            ? File.ReadAllText(legacyInstallation).Trim()
            : NewInstallationId();
        if (installationId.Length == 0)
        {
            installationId = NewInstallationId();
        }

        byte[] sealedKey = Seal(adopted.ExportECPrivateKey());
        File.WriteAllBytes(keyPath, sealedKey);
        File.WriteAllText(installationPath, installationId);

        // Prove the sealed copy is usable before destroying the only other copy.
        try
        {
            byte[] check = Unseal(File.ReadAllBytes(keyPath));
            using var verifier = ECDsa.Create();
            verifier.ImportECPrivateKey(check, out _);
            CryptographicOperations.ZeroMemory(check);
        }
        catch (Exception)
        {
            adopted.Dispose();
            return false;
        }

        try
        {
            File.Delete(legacyKey);
            if (File.Exists(legacyInstallation))
            {
                File.Delete(legacyInstallation);
            }
        }
        catch (IOException)
        {
            // The sealed identity is already in place; a leftover plaintext file is a
            // hygiene problem, not a reason to break the client.
        }

        identity = new DeviceIdentity(adopted, installationId, created: false);
        return true;
    }

    /// <summary>Signs the challenge with the device key (base64, DER sequence).</summary>
    public string SignChallenge(string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        byte[] data = Encoding.UTF8.GetBytes(challenge);
        byte[] signature = _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    private static string DefaultDirectory()
        // Per-user application data, not the profile root: the private key belongs with the
        // application's own data, and %USERPROFILE%\.tolik was the CLI's accidental choice.
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TOLIK",
            "identity");

    private static string NewInstallationId() => Guid.NewGuid().ToString("N");

    private static byte[] Seal(byte[] plaintext)
        => ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    private static byte[] Unseal(byte[] sealedBytes)
    {
        try
        {
            return ProtectedData.Unprotect(sealedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // A corrupted blob and a blob sealed for another Windows account are
            // indistinguishable from here — say both, do not guess which one it is.
            throw new InvalidOperationException(
                "Ключ устройства недоступен: файл повреждён или зашифрован для другого пользователя Windows. Зарегистрируйте устройство заново.");
        }
    }

    public void Dispose() => _key.Dispose();
}
