using System.Security.Cryptography;

namespace TOLIK.Infrastructure.Transport;

/// <summary>
/// Ephemeral per-process ECDsa P-256 key pair for the Core side of the signed agent
/// transport (stage d part 2, docs/INTERFACE-ANALYSIS.md): the WS hello carries the
/// public key so agents can pin it and verify signed command frames. The private key
/// never leaves the process and is regenerated on restart — pinned peer keys are the
/// trust anchor, not this material.
/// </summary>
public sealed class CoreTransportKeys
{
    public const string Kid = "core";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyPem { get; }

    public CoreTransportKeys() => PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>Signs raw bytes with the core transport key (frame sealing).</summary>
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);
}
