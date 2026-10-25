using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;

namespace IntoChat.OS.Modules.Salesforce.Aspire.Hosting;

public static partial class SalesforceHostingExtensions
{
    // An explicit origin overrides the silo's http endpoint. The MCP endpoint is a module option.
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Salesforce.Aspire.Hosting.SalesforceModuleHosting> WithHostedMcp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Salesforce.Aspire.Hosting.SalesforceModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(_ => new SalesforceHostingState());

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class SalesforceHostingState : IntoChatOSModuleProjection
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
                builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ConnectionsPublicOriginKey), origin.AbsoluteUri);
            }
            else
            {
                builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ConnectionsPublicOriginKey), builder.GetEndpoint("http"));
            }
        }
    }
}
