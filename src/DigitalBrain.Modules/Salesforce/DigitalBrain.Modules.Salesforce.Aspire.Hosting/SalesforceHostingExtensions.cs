using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Salesforce.Aspire.Hosting;

public static class SalesforceHostingExtensions
{
    // An explicit origin overrides the silo's http endpoint. The MCP endpoint is a module option.
    public static DigitalBrainModuleBuilder<DigitalBrain.Salesforce.Aspire.Hosting.SalesforceModuleHosting> WithHostedMcp(
        this DigitalBrainModuleBuilder<DigitalBrain.Salesforce.Aspire.Hosting.SalesforceModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(_ => new SalesforceHostingState());

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class SalesforceHostingState : DigitalBrainModuleProjection
    {
        private bool _enabled;
        private Uri? _publicOrigin;

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
