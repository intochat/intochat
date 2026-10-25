using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Salesforce;

// An org connection is the Salesforce neuron's own sealed state; the Platform runs its browser login,
// the code exchange and the refresh through the OAuth method below.
[Module("salesforce")]
[ModuleContracts(typeof(ISalesforce))]
[Integration("salesforce", "Salesforce")]
[Secret("ConsumerKey")]
[Secret("ConsumerSecret")]
public sealed partial class SalesforceModule : Module<SalesforceModuleOptions>
{
    public static ConnectorDefinition[] Connectors => [new("salesforce", "Salesforce", ["org-oauth"], ["salesforce"])
    {
        Neurons = [typeof(ISalesforce)],
        Signals = [typeof(Signals.SalesforceWritePrepared), typeof(Signals.SalesforceWriteFailed), typeof(Signals.SalesforceWriteUncertain), typeof(Signals.RecordWritten)],
        OAuthMethods = [new("org-oauth", "https://login.salesforce.com/services/oauth2/authorize", "https://login.salesforce.com/services/oauth2/token",
            ["mcp_api", "refresh_token"], "salesforce", "ConsumerKey", "ConsumerSecret")],
    }];

    public static readonly Uri DefaultMcpEndpoint = new("https://api.salesforce.com/platform/mcp/v1/platform/sobject-all");

    partial void OnConfigure(IModuleServices services)
    {
        var collection = services.Services;
        collection.TryAddSingleton(TimeProvider.System);
        var options = services.Options<SalesforceModuleOptions>();
        collection.TryAddSingleton(options);
        // Resolved at composition, so a host with a misconfigured MCP endpoint fails to start.
        var endpoint = options.ResolveMcpEndpoint();
        collection.TryAddSingleton<ISalesforceProvider>(_ => new SalesforceMcpProvider(endpoint));
    }
}
