using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

internal sealed class ConnectionsPublicOriginWiring : IntoChatOSModuleProjection
{
    private bool _enabled;
    private Uri? _publicOrigin;

    public static void Ensure(IntoChatOSBuilder builder, Uri? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var state = builder.GetOrAddState(_ => new ConnectionsPublicOriginWiring(), out var added);
        if (added)
        {
            builder.AddProjection(state);
        }
        state.Enable(publicOrigin);
    }

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
