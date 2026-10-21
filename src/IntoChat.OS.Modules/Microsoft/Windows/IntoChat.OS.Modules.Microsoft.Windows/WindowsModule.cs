using System.Reflection;
using System.Runtime.Versioning;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Parts;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.Windows.Signals;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Microsoft.Windows;

// A Windows PC as an edge: the machine, its files and its console, served only by a silo on that PC.
public sealed class WindowsModule() : Module<WindowsOptions>(new ModuleId("windows")), IHttpModule
{
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
        silo.Services.TryAddSingleton<IMachineIdentity, MachineIdentity>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<WindowsNeuron>, WindowsActivation>());
        if (OperatingSystem.IsWindows()
            && Enum.TryParse<MasterKeyCustodyKind>(HostConfiguration.HostKey(silo.Configuration, IntoChatOSNames.CustodyConfigurationKey), ignoreCase: true, out var custody)
            && custody == MasterKeyCustodyKind.Tpm)
        { UseTpmCustody(silo.Services); }
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
