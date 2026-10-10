using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using global::Orleans.Hosting;

namespace DigitalBrain.Microsoft.Orleans;

public sealed class OrleansModule() : Module<OrleansOptions>(new ModuleId("orleans"))
{
    public override System.Reflection.Assembly? Contracts => typeof(ICluster).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("orleans", "Orleans")
        .OffersSecret(OrleansStorageHosting.ClusteringField)
        .OffersSecret(OrleansStorageHosting.GrainStateField)
        .OffersSecret(OrleansStorageHosting.RemindersField)
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
    }
}
