using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using global::Orleans.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

public sealed class OrleansModule() : Module<OrleansOptions>(new ModuleId(OrleansIntegrationNames.Id)), ITopologyModule
{
    public override int Order => -1;

    public override System.Reflection.Assembly? Contracts => typeof(ICluster).Assembly;

    // Clustering, grain state and reminders are no longer integration fields: IntoChatOS:Topology selects them (03-12).
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(OrleansIntegrationNames.Id, "Orleans");

    public static ConnectorDefinition Connector => new("orleans", "Orleans", ["deployment"], ["orleans"])
    {
        Neurons = [typeof(ICluster)],
        Signals = [typeof(ClusterChanged)],
    };

    // The wiring lives in the kernel composition (06-02); this module keeps the legacy cluster neuron until 06-03.
    public IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles => IntoChat.OS.Kernel.Composition.OrleansHostWiring.TopologyProfiles;

    public override void Configure(ISiloBuilder silo) => IntoChat.OS.Kernel.Composition.OrleansHostWiring.Configure(silo);
}
