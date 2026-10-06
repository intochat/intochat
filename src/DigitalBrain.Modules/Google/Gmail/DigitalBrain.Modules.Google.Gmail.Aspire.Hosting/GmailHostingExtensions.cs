using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Google.Gmail;

public static class GmailHostingExtensions
{
    public static DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> WithGmail(
        this DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> module, Action<GmailHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GmailHostingOptions();
        configure(options);
        return module.WithGmail(options.PublicOrigin);
    }

    // Only the kernel receives these values. The public origin defaults to the kernel's own
    // http endpoint; pass one explicitly when a reverse proxy fronts the callback.
    public static DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> WithGmail(
        this DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(brain => new GmailHostingState(brain, module.Resource));

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class GmailHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private const string Root = IntegrationSchema.SectionName + ":google-oauth";

        private bool _enabled;
        private Uri? _publicOrigin;
        private IResourceBuilder<ParameterResource>? _clientId;
        private IResourceBuilder<ParameterResource>? _clientSecret;

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

            _clientId ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("gmail-client-id"))
                .WithDescription(
                    "OAuth client ID for a Google web client configured for the [Gmail MCP server](https://developers.google.com/workspace/gmail/api/guides/configure-mcp-server). "
                    + "Register <your deployment origin>/google/gmail/oauth/callback. This value provisions the Google OAuth Integration.",
                    enableMarkdown: true)
                .WithParentRelationship(module);
            _clientSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("gmail-client-secret"), secret: true)
                .WithDescription(
                    "OAuth client secret for the same Google web client configured for the [Gmail MCP server](https://developers.google.com/workspace/gmail/api/guides/configure-mcp-server). "
                    + "This secret provisions the Google OAuth Integration; named Gmail connections authorize in your browser.",
                    enableMarkdown: true)
                .WithParentRelationship(module);

            builder
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(Root, "ClientId"), _clientId)
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(Root, "ClientSecret"), _clientSecret);
            if (_publicOrigin is { } origin)
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("gmail"), "PublicOrigin"), origin.AbsoluteUri);
            }
            else
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("gmail"), "PublicOrigin"), builder.GetEndpoint("http"));
            }
        }
    }
}
