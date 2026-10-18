using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;

namespace IntoChat.OS.Google.Gmail;

public static partial class GmailHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Google.Gmail.GmailModuleHosting> WithGmail(
        this IntoChatOSModuleBuilder<IntoChat.OS.Google.Gmail.GmailModuleHosting> module, Action<GmailHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GmailHostingOptions();
        configure(options);
        return module.WithGmail(options.PublicOrigin);
    }

    // The public origin defaults to the kernel's own http endpoint; pass one explicitly when a reverse proxy fronts the callback.
    public static IntoChatOSModuleBuilder<IntoChat.OS.Google.Gmail.GmailModuleHosting> WithGmail(
        this IntoChatOSModuleBuilder<IntoChat.OS.Google.Gmail.GmailModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(_ => new GmailHostingState());

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class GmailHostingState : IntoChatOSModuleProjection
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
