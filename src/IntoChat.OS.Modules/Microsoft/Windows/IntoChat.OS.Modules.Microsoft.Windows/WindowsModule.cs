using System.Reflection;
using System.Runtime.Versioning;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Machine;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Parts;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Modules.Microsoft.Windows.Signals;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// A Windows PC as an edge: the machine, its files and its console, served only by a silo on that PC.
public sealed class WindowsModule() : Module<WindowsOptions>(new ModuleId("windows")), IHttpModule
{
    internal const string ManifestName = "windows";

    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<WindowsOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(WindowsModule.ManifestName))],
        Secrets: []);

    public static ConnectorDefinition Connector { get; } = new("windows", "Windows", ["device"], [])
    {
        Neurons = [typeof(IWindows), typeof(IStorage), typeof(IFileSystem), typeof(IConsole)],
        Signals = [typeof(FileWritten)],
        Capabilities = [IStorage.Capability],
    };

    public override Assembly? Contracts => typeof(IWindows).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints) => WindowsEndpoints.Map(endpoints);

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<MachineIdentity>();
        silo.Services.TryAddSingleton<IMachineIdentity>(provider => provider.GetRequiredService<MachineIdentity>());
        silo.Services.TryAddSingleton(MachineKeyStore);
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<WindowsNeuron>, WindowsActivation>());
        if (OperatingSystem.IsWindows()
            && Enum.TryParse<MasterKeyCustodyKind>(HostConfiguration.HostKey(silo.Configuration, IntoChatOSNames.CustodyConfigurationKey), ignoreCase: true, out var custody)
            && custody == MasterKeyCustodyKind.Tpm)
        { UseTpmCustody(silo.Services); }
    }

    // TPM custody when the provider can make keys; otherwise the master-key-sealed file next to the machine-id file,
    // so Custody reports "file" rather than failing.
    private static IMachineKeyStore MachineKeyStore(IServiceProvider provider)
    {
        if (OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable()) { return new TpmMachineKeyStore(); }
        var file = provider.GetRequiredService<MachineIdentity>().File;
        return new FileMachineKeyStore(Path.GetDirectoryName(Path.GetFullPath(file))!, provider.GetRequiredService<IMasterKeyCustody>());
    }

    // The local brain key stays wrapped by the TPM. Missing hardware refuses startup.
    [SupportedOSPlatform("windows")]
    private static void UseTpmCustody(IServiceCollection services)
    {
        services.RemoveAll<IMasterKeyCustody>();
        services.AddSingleton<IMasterKeyCustody>(_ => TpmMasterKeyCustody.IsAvailable()
            ? new TpmMasterKeyCustody()
            : throw new InvalidOperationException("The Microsoft Platform Crypto Provider (TPM) is not available on this machine."));
    }
}
