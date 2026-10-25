using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Identity;

namespace IntoChat.OS.Authority.Connections;

// R146/R155: an app shared into another brain uses its sharer's connection only as the owner's connection recorded it
// at Share (IConnection.ShareWith the app's principal, or its ShareInto alias), and only while the caller and the connection's owner are both members of the brain
// the app runs in; nothing else crosses brains to a connection.
internal sealed class ConnectionBindings(AuthorityNeurons neurons, IIdentityClient identity) : IConnectionBindings
{
    public async ValueTask<bool> IsBound(CallerContext caller, string connectionKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();
        if (caller.AppId is not { Length: > 0 } app) { return false; }
        var callerBrain = BrainScope.Create(caller.AccountId, caller.BrainId).Id;
        if ((await identity.Membership(callerBrain, caller.PersonId))?.AccountId != caller.AccountId) { return false; }
        if (await neurons.BrainAuthority(ConnectionKeys.Parse(connectionKey).BrainId).Account() is not { } owner
            || await identity.ReadAccount(owner) is not { } account
            || (await identity.Membership(callerBrain, account.OwnerPrincipalId))?.AccountId != owner) { return false; }
        // The lookup is the gate's own: unstamped, so the call filter it serves does not evaluate it again.
        var stamped = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            // Shared with the app's principal; an app id outside the caller's brain only matches a legacy pairing.
            var connection = neurons.Connection(connectionKey);
            return BrainKey.BelongsTo(app, callerBrain)
                ? await connection.IsSharedWith(AppPrincipal.Id(app))
                : await connection.IsSharedInto(callerBrain, app);
        }
        finally { if (stamped is not null) { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, stamped); } }
    }
}
