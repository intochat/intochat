using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Modules.Google.Gmail;

public static partial class GmailHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Google.Gmail.GmailModuleHosting> WithGmail(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Google.Gmail.GmailModuleHosting> module, Action<GmailHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GmailHostingOptions();
        configure(options);
        return module.WithGmail(options.PublicOrigin);
    }

    // The public origin defaults to the kernel's own http endpoint; pass one explicitly when a reverse proxy fronts the callback.
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Google.Gmail.GmailModuleHosting> WithGmail(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Google.Gmail.GmailModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ConnectionsPublicOriginWiring.Ensure(module.IntoChatOSBuilder, publicOrigin);
        return module;
    }
}
