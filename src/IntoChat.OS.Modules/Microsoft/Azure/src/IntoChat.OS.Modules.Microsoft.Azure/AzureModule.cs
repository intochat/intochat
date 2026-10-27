using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Microsoft.Azure;

// An Azure subscription as a connection: it ensures the resources the OS and its apps run on.
public sealed class AzureModule() : Module<AzureOptions>(new ModuleId("azure"))
{
    internal const string ManifestName = "azure";

    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => AzureNeurons.Manifest;

    // The connection neuron is plain (06-08): AzureNeurons' assembly [Neuron] classes run on the runtime's host grains.
    public override Type? Neurons => typeof(AzureNeurons);

    public static ConnectorDefinition Connector { get; } = new("azure", "Azure", ["identity"], [])
    {
        Neurons = [typeof(IAzure)],
    };

    public override Assembly? Contracts => typeof(IAzure).Assembly;

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAzureArm, ArmAzure>();
    }
}

// The DigitalBrain module of this connector (06-08): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(AzureModule.ManifestName)]
public sealed class AzureNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AzureOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(AzureModule.ManifestName))],
        Secrets: []);
}
