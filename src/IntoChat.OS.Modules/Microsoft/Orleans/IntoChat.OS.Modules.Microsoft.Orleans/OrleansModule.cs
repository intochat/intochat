using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using global::Orleans.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    // The cloud topology comes from DigitalBrain.Runtime.Orleans.Azure, which only this module references. The production
    // path never replaces the Key Vault source (T-03-59).
    public IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles { get; } =
        [DigitalBrain.Runtime.Orleans.Azure.CloudTopologyExtensions.AddCloudTopology()];

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (CloudStorage.IsCloud(silo.Services)) { CloudStorage.Configure(silo); }
        silo.Services.TryAddSingleton<IClusterMembershipFeed, Cluster.ClusterMembershipFeed>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Cluster.ClusterNeuron>, Cluster.ClusterActivation>());
    }
}
