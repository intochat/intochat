using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Salesforce;

// An org connection is the Salesforce neuron's own sealed state; the Platform runs its browser login,
// the code exchange and the refresh through the OAuth method below.
public sealed class SalesforceModule() : Module<SalesforceModuleOptions>(new ModuleId("salesforce"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Salesforce.ISalesforce).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("salesforce", "Salesforce").RequiresSecret("ConsumerKey").RequiresSecret("ConsumerSecret"); public static ConnectorDefinition[] Connectors => [new("salesforce", "Salesforce", ["org-oauth"], ["salesforce"])
    {
        Neurons = [typeof(ISalesforce)],
        Signals = [typeof(Signals.SalesforceWritePrepared), typeof(Signals.SalesforceWriteFailed), typeof(Signals.SalesforceWriteUncertain), typeof(Signals.RecordWritten)],
        OAuthMethods = [new("org-oauth", "https://login.salesforce.com/services/oauth2/authorize", "https://login.salesforce.com/services/oauth2/token",
            ["mcp_api", "refresh_token"], "salesforce", "ConsumerKey", "ConsumerSecret")],
    }];

    public static readonly Uri DefaultMcpEndpoint = new("https://api.salesforce.com/platform/mcp/v1/platform/sobject-all");

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        var options = silo.Configuration.GetModuleOptions<SalesforceModuleOptions>("salesforce");
        services.TryAddSingleton(options);
        // Resolved at composition, so a host with a misconfigured MCP endpoint fails to start.
        var endpoint = options.ResolveMcpEndpoint();
        services.TryAddSingleton<ISalesforceProvider>(_ => new SalesforceMcpProvider(endpoint));
    }
}
