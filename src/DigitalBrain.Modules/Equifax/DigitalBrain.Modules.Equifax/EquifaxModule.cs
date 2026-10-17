using DigitalBrain.Equifax.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Equifax;

public sealed class EquifaxModule() : Module<EquifaxModuleOptions>(new ModuleId("equifax"))
{
    public const string HttpClientName = "DigitalBrain.Equifax";

    public override System.Reflection.Assembly? Contracts => typeof(IEquifax).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("equifax", "Equifax Peru")
        .RequiresSecret("ClientId").RequiresSecret("ClientSecret").OffersSetting("BaseUrl");

    // "credentials": the pasted value is "ClientId=…;ClientSecret=…", optionally ";BaseUrl=…".
    // "deployment": the value is ignored; the connection reads with the deployment's Equifax integration.
    public static ConnectorDefinition Connector { get; } = new("equifax", "Equifax", ["credentials", "deployment"], ["equifax"])
    {
        Neurons = [typeof(IEquifax)],
        Signals = [typeof(BureauReportRead), typeof(BureauReportFailed)],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        var options = silo.Configuration.GetModuleOptions<EquifaxModuleOptions>("equifax");
        services.TryAddSingleton(options);
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<IEquifaxProvider, HttpEquifaxProvider>();
    }
}
