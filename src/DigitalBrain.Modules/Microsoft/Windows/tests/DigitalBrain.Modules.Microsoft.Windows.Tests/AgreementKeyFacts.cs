using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Microsoft.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

// R127: the machine's agreement key is TPM-held where the provider does ECDH, a software key otherwise.
public sealed class AgreementKeyFacts
{
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheTpmAgreementKeyDerivesTheSameSecretAsItsPeer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmAgreementKey.IsAvailable(), "No TPM with ECDH (Microsoft Platform Crypto Provider) on this machine.");
        var name = $"digitalbrain-test-{Guid.NewGuid():N}-agreement";
        using var tpm = new TpmAgreementKey(name);
        try
        {
            using var peer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var tpmPublic = ECDiffieHellman.Create();
            tpmPublic.ImportSubjectPublicKeyInfo(tpm.PublicKey, out _);

            var fromTpm = tpm.DeriveShared(peer.ExportSubjectPublicKeyInfo());
            var fromPeer = peer.DeriveKeyFromHash(tpmPublic.PublicKey, HashAlgorithmName.SHA256);

            Assert.Equal(fromPeer, fromTpm);
        }
        finally { tpm.Delete(); }
    }

    [Fact]
    public void TheSoftwareAgreementKeyIsUsedWhenTheProviderHasNoEcdh()
    {
        var software = SoftwareMachineAgreementKey.LoadOrCreate(Path.Combine(Directory.CreateTempSubdirectory("agreement").FullName, "agreement.key"));
        var services = new ServiceCollection();
        services.AddSingleton<IMachineAgreementKey>(software);

        WindowsModule.UseTpmAgreementKey(services, providerHasEcdh: () => false);

        using var provider = services.BuildServiceProvider();
        Assert.Same(software, provider.GetRequiredService<IMachineAgreementKey>());
    }
}
