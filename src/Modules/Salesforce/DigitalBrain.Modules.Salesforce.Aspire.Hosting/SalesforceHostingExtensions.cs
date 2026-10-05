using DigitalBrain;
using DigitalBrain.Framework;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Salesforce.Aspire.Hosting;

public static class SalesforceHostingExtensions
{
    // Only the kernel receives the OAuth parameters. The public origin defaults to the kernel's own http endpoint;
    // the MCP endpoint is a module option.
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
        private static readonly string OAuthRoot = SalesforceOAuthOptions.SectionName;

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
                    + "Register <your deployment origin>/integrations/salesforce/callback, enable PKCE and JWT access tokens, and allow mcp_api and refresh_token.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _consumerSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("salesforce-consumer-secret"), secret: true)
                .WithDescription(
                    "Consumer secret from the same Salesforce External Client App. Enable Require Secret for Web Server Flow. "
                    + "Only the kernel receives this secret; Salesforce login happens in your browser when the assistant needs access.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(OAuthRoot, "ConsumerKey"), _consumerKey)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(OAuthRoot, "ConsumerSecret"), _consumerSecret);
            if (_publicOrigin is { } origin)
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(OAuthRoot, "PublicOrigin"), origin.AbsoluteUri);
            }
            else
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(OAuthRoot, "PublicOrigin"), builder.GetEndpoint("http"));
            }
        }
    }
}
