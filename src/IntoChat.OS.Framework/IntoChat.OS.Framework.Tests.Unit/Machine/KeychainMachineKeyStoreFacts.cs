using IntoChat.OS.Framework.Machine;
using IntoChat.OS.Testing.Conformance.Machine;

namespace IntoChat.OS.Framework.Tests.Unit.Machine;

// Manual until a macOS runner exists: on any other OS every fact skips.
[Trait("Platform", "macOS")]
public sealed class KeychainMachineKeyStoreFacts : MachineKeyStoreConformance, IDisposable
{
    private readonly string _account = "conformance-" + Guid.NewGuid().ToString("N");

    protected override string? SkipReason => OperatingSystem.IsMacOS() ? null : "The keychain store runs only on macOS.";

    protected override IMachineKeyStore CreateStore() =>
        OperatingSystem.IsMacOS() ? new KeychainMachineKeyStore(_account, "intochat-os-machine-key-test") : throw new PlatformNotSupportedException();

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS()) { new KeychainMachineKeyStore(_account, "intochat-os-machine-key-test").DeleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
    }
}
