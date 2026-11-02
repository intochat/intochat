using DigitalBrain.Capabilities;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Connections;

// R146/R155 + R6.3: an app shared into another brain or a trusted friend app uses a shared connection only as the owner's
// connection or EdgeGrantsCache recorded it (IConnection.ShareWith the app's principal, its ShareInto alias, or friend pairing).
internal sealed class ConnectionBindings(AuthorityNeurons neurons, IIdentityClient identity, Authority.Identity.Grants.IEdgeGrantsCache? edgeCache = null) : IConnectionBindings
{
    private readonly Authority.Identity.Grants.IEdgeGrantsCache _edgeCache = edgeCache ?? Authority.Identity.Grants.EdgeGrantsCache.Current;

    public async ValueTask<bool> IsBound(CapabilityCaller caller, string connectionKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();
        if (caller.AppId is not { Length: > 0 } app) { return false; }
        if (await _edgeCache.IsConnectionBoundAsync(caller.BrainId, caller, connectionKey, cancellationToken).ConfigureAwait(false)) { return true; }

        var callerBrain = BrainScope.Create(caller.AccountId, caller.BrainId).Id;
        if ((await identity.Membership(callerBrain, caller.PersonId))?.AccountId != caller.AccountId) { return false; }
        if (await neurons.Directory().OwnerOfBrain(ConnectionKeys.Parse(connectionKey).BrainId) is not { } account
            || (await identity.Membership(callerBrain, account.OwnerPrincipalId))?.AccountId != account.AccountId) { return false; }
        // The lookup is the gate's own: unstamped, so the call filter it serves does not evaluate it again.
        var stamped = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            // Shared with the app's principal; an app id outside the caller's brain only matches a legacy pairing.
            var connection = neurons.Connection(connectionKey);
            var direct = BrainKey.BelongsTo(app, callerBrain)
                ? await connection.IsSharedWith(AppPrincipal.Id(app))
                : await connection.IsSharedInto(callerBrain, app);
            if (direct) { return true; }

            var owners = await _edgeCache.OwnersSharingWithAsync(app, connectionKey, cancellationToken).ConfigureAwait(false);
            foreach (var ownerApp in owners)
            {
                if (await connection.IsSharedWith(AppPrincipal.Id(ownerApp)))
                {
                    return true;
                }
            }
            return false;
        }
        finally { if (stamped is not null) { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, stamped); } }
    }
}
