using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Modules.Salesforce.Aspire.Hosting;

public static partial class SalesforceHostingExtensions
{
    // An explicit origin overrides the silo's http endpoint. The MCP endpoint is a module option.
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Salesforce.Aspire.Hosting.SalesforceModuleHosting> WithHostedMcp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Salesforce.Aspire.Hosting.SalesforceModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ConnectionsPublicOriginWiring.Ensure(module.IntoChatOSBuilder, publicOrigin);
        return module;
    }
}
