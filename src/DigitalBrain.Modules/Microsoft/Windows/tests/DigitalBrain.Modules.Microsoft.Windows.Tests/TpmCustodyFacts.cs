using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

// Live-gated: these run against this PC's TPM through the Microsoft Platform Crypto Provider and skip where there is none.
// The class is Windows-only for the analyzer; each fact still skips first where the provider is missing.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TpmCustodyFacts
{
    [Fact]
    public void AWrappedSecretUnwrapsThroughTheSameTpmKey()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var account = $"test-{Guid.NewGuid():N}";
        var custody = new TpmMasterKeyCustody();
        try
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            var wrapped = custody.Wrap(account, "part|x", secret);

            Assert.Equal(secret, custody.Unwrap(account, "part|x", wrapped));
            Assert.ThrowsAny<CryptographicException>(() => { custody.Unwrap(account, "part|y", wrapped); });
            Assert.ThrowsAny<CryptographicException>(() => { custody.Unwrap($"other-{Guid.NewGuid():N}", "part|x", wrapped); });
        }
        finally { TpmMasterKeyCustody.Delete(account); }
    }

    [Fact]
    public void TheTpmKeyIsNotExportable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var account = $"test-{Guid.NewGuid():N}";
        try
        {
            using var key = TpmMasterKeyCustody.OpenOrCreate(account);
            using var rsa = new RSACng(key);

            Assert.ThrowsAny<CryptographicException>(() => rsa.ExportParameters(includePrivateParameters: true));
            Assert.Equal(2048, rsa.KeySize);
        }
        finally { TpmMasterKeyCustody.Delete(account); }
    }

    [Fact]
    public void TheMachineSignsHeadsWithItsTpmKey()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var name = $"digitalbrain-test-machine-{Guid.NewGuid():N}";
        using var machine = new TpmMachineSigningKey(name);
        try
        {
            // The bytes a head record signs; HeadRecord itself is Platform-internal.
            var head = Encoding.UTF8.GetBytes("part-hash|1|holder|lease|payload");
            var signature = machine.Sign(head);

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(machine.PublicKey, out _);
            Assert.Equal(256, verifier.KeySize);
            Assert.True(verifier.VerifyData(head, signature, HashAlgorithmName.SHA256));
            Assert.False(verifier.VerifyData((byte[])[.. head, 0], signature, HashAlgorithmName.SHA256));
            using var again = new TpmMachineSigningKey(name);
            Assert.Equal(machine.PublicKey, again.PublicKey);
        }
        finally { machine.Delete(); }
    }
}
