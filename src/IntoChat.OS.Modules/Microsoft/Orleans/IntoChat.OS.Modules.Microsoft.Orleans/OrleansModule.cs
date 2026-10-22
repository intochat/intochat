using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using global::Orleans.Hosting;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Microsoft.Orleans;

public sealed class OrleansModule() : Module<OrleansOptions>(new ModuleId(OrleansIntegrationNames.Id))
{
    public override int Order => -1;

    public override System.Reflection.Assembly? Contracts => typeof(ICluster).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(OrleansIntegrationNames.Id, "Orleans")
        .OffersSecret(OrleansStorageHosting.ClusteringField)
        .OffersSecret(OrleansStorageHosting.GrainStateField)
        .OffersSecret(OrleansStorageHosting.RemindersField)
        .OffersSetting(OrleansStorageHosting.ClusteringField + StorageEndpoint.UriSuffix)
        .OffersSetting(OrleansStorageHosting.GrainStateField + StorageEndpoint.UriSuffix)
        .OffersSetting(OrleansStorageHosting.RemindersField + StorageEndpoint.UriSuffix)
        .OffersSetting(StorageEndpoint.ManagedIdentityClientIdField)
        .OffersSetting(OrleansStorageHosting.GrainStateContainerField);

    public static ConnectorDefinition Connector => new("orleans", "Orleans", ["deployment"], ["orleans"])
    {
        Neurons = [typeof(ICluster)],
        Signals = [typeof(ClusterChanged)],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddOrleansStorage(silo.Configuration);
        silo.Services.TryAddSingleton<IClusterMembershipFeed, Cluster.ClusterMembershipFeed>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Cluster.ClusterNeuron>, Cluster.ClusterActivation>());
    }
}
