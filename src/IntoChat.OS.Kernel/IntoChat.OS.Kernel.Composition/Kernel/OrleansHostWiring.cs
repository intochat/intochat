using global::Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Modules.Microsoft.Orleans.Cluster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition;

// The Orleans host wiring the former Microsoft/Orleans module carried (06-02, R4-12): the cloud topology implementation,
// the deployment's grain-state container on cloud, and the legacy cluster neuron's membership feed until 06-03 moves it.
public static class OrleansHostWiring
{
    // The cloud topology comes from DigitalBrain.Runtime.Orleans.Azure, which only the composition references. The production
    // path never replaces the Key Vault source (T-03-59).
    public static IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles { get; } =
        [DigitalBrain.Runtime.Orleans.Azure.CloudTopologyExtensions.AddCloudTopology()];

    public static void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (CloudStorage.IsCloud(silo.Services)) { CloudStorage.Configure(silo); }
        silo.Services.TryAddSingleton<IClusterMembershipFeed, ClusterMembershipFeed>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<ClusterNeuron>, ClusterActivation>());
    }
}
