using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Connections.Hosting;

// CallFilter's binding hook over the connection bindings: cross-brain shared connections and friend-shared connections.
internal sealed class ConnectionCallBindings(IConnectionBindings bindings, Authority.Identity.Grants.IEdgeGrantsCache? edgeCache = null) : ICallBindings
{
    private readonly Authority.Identity.Grants.IEdgeGrantsCache _edgeCache = edgeCache ?? Authority.Identity.Grants.EdgeGrantsCache.Current;

    public async ValueTask<bool> IsBound(CallRequest request, string callerScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var slash = request.TargetNeuron.IndexOf('/', StringComparison.Ordinal);
        var key = slash < 0 ? "" : request.TargetNeuron[(slash + 1)..];
        if (!ConnectionKeys.Is(key)) { return false; }
        string brain;
        try { brain = ConnectionKeys.Parse(key).BrainId; }
        catch (ArgumentException) { return false; }
        var capCaller = request.Caller.ToCapabilityCaller();
        if (capCaller is null) { return false; }
        if (await _edgeCache.IsConnectionBoundAsync(brain, capCaller, key, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        var callerBrain = request.Caller.Brain.Value;
        if (!string.IsNullOrWhiteSpace(callerBrain)
            && !string.Equals(callerBrain, brain, StringComparison.OrdinalIgnoreCase)
            && await _edgeCache.IsConnectionBoundAsync(callerBrain, capCaller, key, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return brain != callerScope && await bindings.IsBound(capCaller, key, cancellationToken).ConfigureAwait(false);
    }
}
