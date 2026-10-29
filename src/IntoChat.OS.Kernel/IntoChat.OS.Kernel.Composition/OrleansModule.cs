using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Orleans.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

public sealed class OrleansModule() : Module<OrleansOptions>(new ModuleId(OrleansIntegrationNames.Id)), ITopologyModule
{
    public override int Order => -1;

    public override System.Reflection.Assembly? Contracts => typeof(ICluster).Assembly;

    // The cluster neuron is a plain neuron of the kernel composition (06-08).
    public override Type? Neurons => typeof(OrleansNeurons);

    // Clustering, grain state and reminders are no longer integration fields: IntoChatOS:Topology selects them (03-12).
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(OrleansIntegrationNames.Id, "Orleans");

    public static ConnectorDefinition Connector => new("orleans", "Orleans", ["deployment"], ["orleans"])
    {
        Neurons = [typeof(ICluster)],
        Signals = [typeof(ClusterChanged)],
    };

    // The wiring lives in the kernel composition (06-02); this module keeps the legacy cluster neuron until 06-03.
    public IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles => IntoChat.OS.Kernel.Composition.OrleansHostWiring.TopologyProfiles;

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) => IntoChat.OS.Kernel.Composition.OrleansHostWiring.ConfigureServices(services);
}

// The DigitalBrain module of the kernel composition's plain neurons (06-08): the cluster neuron.
[DigitalBrain.Module(OrleansIntegrationNames.Id)]
public sealed class OrleansNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
