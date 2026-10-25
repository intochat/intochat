using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Connections.Hosting;

// CallFilter's binding hook over the connection bindings: only a connection key of another brain can be bound.
internal sealed class ConnectionCallBindings(IConnectionBindings bindings) : ICallBindings
{
    public async ValueTask<bool> IsBound(CallRequest request, string callerScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var slash = request.TargetNeuron.IndexOf('/', StringComparison.Ordinal);
        var key = slash < 0 ? "" : request.TargetNeuron[(slash + 1)..];
        if (!ConnectionKeys.Is(key)) { return false; }
        string brain;
        try { brain = ConnectionKeys.Parse(key).BrainId; }
        catch (ArgumentException) { return false; }
        return brain != callerScope && await bindings.IsBound(request.Caller, key, cancellationToken).ConfigureAwait(false);
    }
}
