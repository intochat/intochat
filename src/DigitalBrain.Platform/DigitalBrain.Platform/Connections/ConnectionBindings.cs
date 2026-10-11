using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Platform.Connections;

// R146: an app shared into another brain uses its sharer's connection only as the owner's connection recorded it at
// Share (IConnection.ShareInto); nothing else crosses brains to a connection.
internal sealed class ConnectionBindings(IGrainFactory grains) : IConnectionBindings
{
    public async ValueTask<bool> IsBound(string callerBrainId, string callerApp, string connectionKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The lookup is the gate's own: unstamped, so the call filter it serves does not evaluate it again.
        var caller = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try { return await grains.GetConnection(connectionKey).IsSharedInto(callerBrainId, callerApp); }
        finally { if (caller is not null) { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, caller); } }
    }
}
