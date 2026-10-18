using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Google.Gmail;

public sealed class GmailModuleHosting : ModuleHosting<IntoChat.OS.Google.Gmail.GmailModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
    }
}
