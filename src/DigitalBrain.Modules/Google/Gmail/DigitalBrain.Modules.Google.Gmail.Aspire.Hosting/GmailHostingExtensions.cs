using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;

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

    // The public origin defaults to the kernel's own http endpoint; pass one explicitly when a reverse proxy fronts the callback.
    public static DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> WithGmail(
        this DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting> module,
        Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(_ => new GmailHostingState());

        state.Enable(publicOrigin);
        return module;
    }

    private sealed class GmailHostingState : DigitalBrainModuleProjection
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
