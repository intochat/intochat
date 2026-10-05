using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Auth;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Salesforce;

public sealed class SalesforceModule() : Module<SalesforceModuleOptions>(new ModuleId("salesforce")), IHttpModule
{
    public static IntegrationSchema[] Integrations => [IntegrationSchema.For("salesforce", "Salesforce").RequiresSecret("ConsumerKey").RequiresSecret("ConsumerSecret")];
    public static ConnectorDefinition[] Connectors => [new("salesforce", "Salesforce", ["org-oauth"], ["salesforce"])
    {
        Neurons = [typeof(ISalesforce)],
        Signals = [typeof(Signals.SalesforceConnected), typeof(Signals.SalesforceConnectionRejected), typeof(Signals.SalesforceDisconnected), typeof(Signals.SalesforceRefreshed), typeof(Signals.SalesforceWritePrepared), typeof(Signals.SalesforceWriteFailed), typeof(Signals.SalesforceWriteUncertain), typeof(Signals.RecordWritten)],
    }];

    public static readonly Uri DefaultMcpEndpoint = new("https://api.salesforce.com/platform/mcp/v1/platform/sobject-all");

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<SalesforceModuleOptions>().Bind(silo.Configuration.GetSection(DigitalBrainConfiguration.ModuleOptionsSection(new ModuleId("salesforce"))));
        services.TryAddSingleton<SalesforceOAuthConfiguration>();
        services.TryAddSingleton<SalesforceLogins>();
        services.AddSingleton<BrowserLogins>(s => s.GetRequiredService<SalesforceLogins>());
        services.TryAddSingleton<ISalesforceTokenExchange, SalesforceTokenExchange>();
        services.TryAddSingleton<SalesforceTokenRefresh>();
        services.TryAddSingleton<SalesforceCredentialStore>();
        services.TryAddSingleton<SalesforceWriteAccess>();
        var moduleOptions = silo.Configuration.GetModuleOptions<SalesforceModuleOptions>("salesforce");
        services.AddOptions<SalesforceMcpOptions>()
            .Configure(mcp =>
            {
                mcp.Endpoint = (moduleOptions.McpEndpoint ?? DefaultMcpEndpoint).AbsoluteUri;
                mcp.AllowLoopback = moduleOptions.UseLocalMcp;
            })
            .Validate(static options => { _ = options.ResolveEndpoint(); return true; })
            .ValidateOnStart();
        services.TryAddSingleton<ISalesforceProvider>(static services => new SalesforceMcpProvider(
            services.GetRequiredService<IOptions<SalesforceMcpOptions>>().Value.ResolveEndpoint()));
        services.AddSingleton<IHttpSurface>(static services => new BrowserLoginSurface(services.GetRequiredService<SalesforceLogins>()));
        services.AddSalesforceAuthentication(SalesforceLogins.LoginDefinition);
        services.AddSingleton<IExternalConnection, SalesforceExternalConnection>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        SalesforceConnectionEndpoints.Map(endpoints);
    }
}
