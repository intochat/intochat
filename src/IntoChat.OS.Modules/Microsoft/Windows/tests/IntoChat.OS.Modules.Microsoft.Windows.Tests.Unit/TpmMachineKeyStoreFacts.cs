using System.Runtime.Versioning;
using IntoChat.OS.Framework.Machine;
using IntoChat.OS.Testing.Conformance.Machine;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// Runs against the real Platform Crypto Provider under a throwaway key name; skips without Windows or a usable TPM.
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class TpmMachineKeyStoreFacts : MachineKeyStoreConformance, IDisposable
{
    private static readonly Lazy<bool> Available = new(() => OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable());
    private readonly string _name = TpmMasterKeyCustody.DefaultPrefix + "machine-key-test-" + Guid.NewGuid().ToString("N");

    protected override string? SkipReason => Available.Value ? null : "The Microsoft Platform Crypto Provider (TPM) is not available here.";

    protected override IMachineKeyStore CreateStore() =>
        OperatingSystem.IsWindows() ? new TpmMachineKeyStore(_name) : throw new PlatformNotSupportedException();

    public void Dispose()
    {
        if (Available.Value && OperatingSystem.IsWindows()) { new TpmMachineKeyStore(_name).DeleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
    }

    [Fact]
    public void TheDefaultKeyKeepsTheTpmPrefix()
    {
        Assert.Equal("intochat-os-", TpmMasterKeyCustody.DefaultPrefix);
        Assert.Equal("intochat-os-machine-key", TpmMachineKeyStore.DefaultName);
    }

    [Fact]
    public void TheStoreReportsTpmCustody()
    {
        if (SkipReason is { } reason) { Assert.Skip(reason); }
        Assert.Equal("tpm", CreateStore().Custody);
    }
}
