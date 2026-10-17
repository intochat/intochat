using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.RiskSeal;

// A RiskSeal connection either pastes its own api-key (sealed in the neuron's state) or uses the
// "deployment" method, which scores with the deployment's integration. The module maps no routes.
public sealed class RiskSealModule() : Module<RiskSealModuleOptions>(new ModuleId("riskseal"))
{
    internal const string HttpClientName = "DigitalBrain.RiskSeal";

    public override System.Reflection.Assembly? Contracts => typeof(IRiskSeal).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("riskseal", "RiskSeal").RequiresSecret("ApiKey").OffersSetting("BaseUrl");

    internal const string DeploymentMethod = "deployment";

    public static ConnectorDefinition Connector { get; } = new("riskseal", "RiskSeal", ["api-key", DeploymentMethod], ["riskseal"])
    {
        Neurons = [typeof(IRiskSeal)],
        Signals = [typeof(Signals.RiskScored), typeof(Signals.RiskScoreFailed)],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        var options = silo.Configuration.GetModuleOptions<RiskSealModuleOptions>("riskseal");
        options.Validate();
        services.TryAddSingleton(options);
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<IRiskSealProvider, HttpRiskSealProvider>();
    }
}
