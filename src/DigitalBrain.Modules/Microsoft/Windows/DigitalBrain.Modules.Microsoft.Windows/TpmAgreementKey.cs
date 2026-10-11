using System.Runtime.Versioning;
using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Microsoft.Windows;

// R127: this machine's P-256 agreement key, persisted non-exportable in the TPM; created on first use, then reopened.
[SupportedOSPlatform("windows")]
internal sealed class TpmAgreementKey : IMachineAgreementKey, IDisposable
{
    public const string DefaultName = "digitalbrain-agreement";

    private readonly CngKey _key;
    private readonly ECDiffieHellmanCng _agreement;

    public TpmAgreementKey(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _key = CngKey.Exists(name, TpmMasterKeyCustody.Provider)
            ? CngKey.Open(name, TpmMasterKeyCustody.Provider)
            : CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, name, new CngKeyCreationParameters
            {
                Provider = TpmMasterKeyCustody.Provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.KeyAgreement,
            });
        _agreement = new ECDiffieHellmanCng(_key);
        PublicKey = _agreement.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }

    public byte[] DeriveShared(byte[] ephemeralPublicKey) => SoftwareMachineAgreementKey.DeriveWith(_agreement, ephemeralPublicKey);

    // Whether the provider makes and uses ECDH keys: a throwaway named key derives once, then is deleted.
    public static bool IsAvailable()
    {
        try
        {
            using var probe = new TpmAgreementKey($"digitalbrain-probe-{Guid.NewGuid():N}");
            try
            {
                using var peer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                probe.DeriveShared(peer.ExportSubjectPublicKeyInfo());
                return true;
            }
            finally { probe.Delete(); }
        }
        catch (CryptographicException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    internal void Delete() => _key.Delete();

    public void Dispose()
    {
        _agreement.Dispose();
        _key.Dispose();
    }
}
