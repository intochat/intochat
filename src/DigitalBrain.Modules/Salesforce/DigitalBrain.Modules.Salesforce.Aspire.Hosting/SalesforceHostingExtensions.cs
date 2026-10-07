using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Salesforce.Aspire.Hosting;

public static class SalesforceHostingExtensions
{
    // Only the host receives the OAuth parameters. The Platform's public origin defaults to the host's own
    // http endpoint; pass one explicitly when a reverse proxy fronts the callback. The MCP endpoint is a module option.
    public static DigitalBrainModuleBuilder<DigitalBrain.Salesforce.Aspire.Hosting.SalesforceModuleHosting> WithHostedMcp(
        this DigitalBrainModuleBuilder<DigitalBrain.Salesforce.Aspire.Hosting.SalesforceModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(brain => new SalesforceHostingState(brain, module.Resource));

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class SalesforceHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private bool _enabled;
        private Uri? _publicOrigin;
        private IResourceBuilder<ParameterResource>? _consumerKey;
        private IResourceBuilder<ParameterResource>? _consumerSecret;

        internal void Enable(Uri? publicOrigin)
        {
            _enabled = true;
            _publicOrigin = publicOrigin ?? _publicOrigin;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (!_enabled)
            {
                return;
            }

            _consumerKey ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("salesforce-consumer-key"), secret: true)
                .WithDescription(
                    "Consumer key (client ID) from your existing Salesforce "
                    + "[External Client App](https://developer.salesforce.com/docs/platform/hosted-mcp-servers/guide/create-external-client-app.html). "
                    + "Register <your deployment origin>/connections/oauth/salesforce/callback, enable PKCE and JWT access tokens, and allow mcp_api and refresh_token.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _consumerSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("salesforce-consumer-secret"), secret: true)
                .WithDescription(
                    "Consumer secret from the same Salesforce External Client App. Enable Require Secret for Web Server Flow. "
                    + "Only the host receives this secret; Salesforce login happens in your browser when the assistant needs access.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("salesforce", "ConsumerKey")), _consumerKey)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("salesforce", "ConsumerSecret")), _consumerSecret);
            if (_publicOrigin is { } origin)
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ConnectionsPublicOriginKey), origin.AbsoluteUri);
            }
            else
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ConnectionsPublicOriginKey), builder.GetEndpoint("http"));
            }
        }
    }
}
