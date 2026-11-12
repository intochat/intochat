using System.Runtime.Versioning;
using DigitalBrain.Identity.Local.Custody;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Framework.Machine;

// macOS: keychain custody; Linux and containers: the sealed 0600 file. Windows registration belongs to the Windows
// module (TPM when available, else the file store); TryAdd keeps whichever registered first.
public static class MachineHosting
{
    public static IServiceCollection AddMachineIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (OperatingSystem.IsWindows()) { return services; }
        services.TryAddSingleton<IMachineIdentity>(provider => new FileMachineIdentity(provider.GetService<IConfiguration>()?[MachineIdFile.ConfigurationKey]));
        if (OperatingSystem.IsMacOS())
        {
            UseKeychain(services);
        }
        else
        {
            services.TryAddSingleton<IMachineKeyStore>(provider =>
            {
                var file = MachineIdFile.Resolve(provider.GetService<IConfiguration>()?[MachineIdFile.ConfigurationKey]);
                return new FileMachineKeyStore(Path.GetDirectoryName(Path.GetFullPath(file))!, provider.GetRequiredService<IMasterKeyCustody>());
            });
        }
        return services;
    }

    [SupportedOSPlatform("macos")]
    private static void UseKeychain(IServiceCollection services) =>
        services.TryAddSingleton<IMachineKeyStore>(provider => Keychain(provider.GetRequiredService<IMachineIdentity>().Id));

    [SupportedOSPlatform("macos")]
    private static KeychainMachineKeyStore Keychain(string account) => new(account);
}
