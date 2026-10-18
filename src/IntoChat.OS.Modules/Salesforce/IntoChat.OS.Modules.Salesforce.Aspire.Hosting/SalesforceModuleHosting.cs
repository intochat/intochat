using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Salesforce.Aspire.Hosting;

public sealed class SalesforceModuleHosting : ModuleHosting<IntoChat.OS.Salesforce.SalesforceModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
    }
}
