using System.Reflection;
using DigitalBrain.Framework;
using System.Runtime.Versioning;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Windows.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Windows;

// A Windows PC as an edge: the machine, its files and its console, served only by a silo on that PC.
public sealed class WindowsModule() : Module<WindowsOptions>(new ModuleId("windows"))
{
    public static ConnectorDefinition Connector { get; } = new("windows", "Windows", ["device"], [])
    {
        Neurons = [typeof(IWindows), typeof(IFileSystem), typeof(IConsole)],
        Signals = [typeof(FileWritten)],
    };

    public override Assembly? Contracts => typeof(IWindows).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<IMachineIdentity, MachineIdentity>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<WindowsNeuron>, WindowsActivation>());
        if (OperatingSystem.IsWindows()
            && Enum.TryParse<MasterKeyCustodyKind>(silo.Configuration[DigitalBrainNames.CustodyConfigurationKey], ignoreCase: true, out var custody)
            && custody == MasterKeyCustodyKind.Tpm)
        { UseTpmCustody(silo.Services); }
    }

    // Replaces the platform's file key and configured custody whichever registered first. Without a TPM the factory
    // throws, and the platform's start validation refuses the machine naming the custody key.
    [SupportedOSPlatform("windows")]
    private static void UseTpmCustody(IServiceCollection services)
    {
        services.RemoveAll<IMasterKeyCustody>();
        services.RemoveAll<IMachineSigningKey>();
        services.AddSingleton<IMasterKeyCustody>(_ => TpmMasterKeyCustody.IsAvailable()
            ? new TpmMasterKeyCustody()
            : throw new InvalidOperationException("The Microsoft Platform Crypto Provider (TPM) is not available on this machine."));
        services.AddSingleton<IMachineSigningKey>(_ => new TpmMachineSigningKey());
        UseTpmAgreementKey(services, TpmAgreementKey.IsAvailable);
    }

    // R127: the agreement key moves into the TPM only where its provider does ECDH; otherwise the platform's software
    // key stays.
    internal static void UseTpmAgreementKey(IServiceCollection services, Func<bool> providerHasEcdh)
    {
        if (!OperatingSystem.IsWindows() || !providerHasEcdh()) { return; }
        services.RemoveAll<IMachineAgreementKey>();
        services.AddSingleton<IMachineAgreementKey>(_ => OperatingSystem.IsWindows() ? new TpmAgreementKey() : throw new PlatformNotSupportedException());
    }
}
