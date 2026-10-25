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
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AzureOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(AzureModule.ManifestName))],
        Secrets: []);

    public static ConnectorDefinition Connector { get; } = new("azure", "Azure", ["identity"], [])
    {
        Neurons = [typeof(IAzure)],
    };

    public override Assembly? Contracts => typeof(IAzure).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<IAzureArm, ArmAzure>();
    }
}
