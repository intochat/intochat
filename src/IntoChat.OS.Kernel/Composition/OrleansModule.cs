using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Cluster;

namespace IntoChat.OS.Kernel.Composition;

[Module(OrleansIntegrationNames.Id)]
public sealed class OrleansModule : Module, ITopologyModule
{
    public static int Order => -1;

    public static System.Reflection.Assembly? Contracts => typeof(ICluster).Assembly;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [Setting.Of<OrleansOptions>(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(OrleansIntegrationNames.Id))],
        Secrets: []);

    // Clustering, grain state and reminders are no longer integration fields: IntoChatOS:Topology selects them (03-12).
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(OrleansIntegrationNames.Id, "Orleans");

    public static ConnectorDefinition Connector => new("orleans", "Orleans", ["deployment"], ["orleans"])
    {
        Neurons = [typeof(ICluster)],
        Signals = [typeof(ClusterChanged)],
    };

    // The wiring lives in the kernel composition (06-02).
    public IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles => OrleansHostWiring.TopologyProfiles;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        OrleansHostWiring.ConfigureServices(services);
    }
}
