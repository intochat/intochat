using DigitalBrain.Identity.Local.Custody;
using IntoChat.OS.Framework.Machine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition.Machine;

// The IMachineIdentity and IMachineKeyStore registrations from the Windows module (Task 24):
// IMachineIdentity and IMachineKeyStore stay OS-only while the Windows module depends only on DigitalBrain.
public static class WindowsMachineHosting
{
    public static IServiceCollection AddWindowsMachineIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new MachineIdentity(provider.GetService<IConfiguration>()));
        services.TryAddSingleton<IMachineIdentity>(provider => provider.GetRequiredService<MachineIdentity>());
        services.TryAddSingleton(MachineKeyStore);
        return services;
    }

    // TPM custody when the provider can make keys; otherwise the master-key-sealed file next to the machine-id file,
    // so Custody reports "file" rather than failing.
    private static IMachineKeyStore MachineKeyStore(IServiceProvider provider)
    {
        if (OperatingSystem.IsWindows() && TpmMachineKeyStore.IsAvailable()) { return new TpmMachineKeyStore(); }
        var file = provider.GetRequiredService<MachineIdentity>().File;
        return new FileMachineKeyStore(Path.GetDirectoryName(Path.GetFullPath(file))!, provider.GetRequiredService<IMasterKeyCustody>());
    }
}
