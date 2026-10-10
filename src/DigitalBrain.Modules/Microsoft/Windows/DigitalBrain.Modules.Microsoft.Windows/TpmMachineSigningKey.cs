using System.Runtime.Versioning;
using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Microsoft.Windows;

// R35: this machine's P-256 signing key, persisted non-exportable in the TPM; created on first use, then reopened.
[SupportedOSPlatform("windows")]
internal sealed class TpmMachineSigningKey : IMachineSigningKey, IDisposable
{
    public const string DefaultName = "digitalbrain-machine";

    private readonly CngKey _key;
    private readonly ECDsaCng _signer;

    public TpmMachineSigningKey(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _key = CngKey.Exists(name, TpmMasterKeyCustody.Provider)
            ? CngKey.Open(name, TpmMasterKeyCustody.Provider)
            : CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = TpmMasterKeyCustody.Provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            });
        _signer = new ECDsaCng(_key);
        PublicKey = _signer.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }

    public byte[] Sign(byte[] data) => _signer.SignData(data, HashAlgorithmName.SHA256);

    internal void Delete() => _key.Delete();

    public void Dispose()
    {
        _signer.Dispose();
        _key.Dispose();
    }
}
