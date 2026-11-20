using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Callers;

namespace IntoChat.OS.Authority.Connections;

// R146/R155 + R6.3: an app shared into another brain uses a shared connection only as the owner's
// connection or EdgeGrantsCache recorded it (IConnection.ShareWith the app's principal, or its ShareInto alias).
internal sealed class ConnectionBindings(AuthorityNeurons neurons, IIdentityClient identity, ICallerAmbient callers, EdgeAuthority edge,
    Authority.Identity.Grants.IEdgeGrantsCache? edgeCache = null) : IConnectionBindings
{
    private readonly Authority.Identity.Grants.IEdgeGrantsCache _edgeCache = edgeCache ?? Authority.Identity.Grants.EdgeGrantsCache.Current;

    public async ValueTask<bool> IsBound(string connectionKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only an app, or an agent as the assistant, is bound; and only for the person it acts for.
        if (callers.Current is not { } caller || Authority.Identity.Grants.GrantRules.AppOf(caller) is not { } app || caller.For is not { } person) { return false; }
        if (await _edgeCache.IsConnectionBoundAsync(caller.Brain.Value, caller, connectionKey, cancellationToken).ConfigureAwait(false)) { return true; }

        var callerBrain = caller.Brain.Value;
        var connectionBrain = ConnectionKeys.Parse(connectionKey).BrainId;
        if ((await identity.Membership(callerBrain, person.Principal))?.AccountId != person.Account) { return false; }
        if (await identity.OwnerOfBrain(connectionBrain) is not { } account
            || (await identity.Membership(callerBrain, account.OwnerPrincipalId))?.AccountId != account.AccountId) { return false; }
        // The lookup is the gate's own, made as the kernel in the connection's brain, so the call filter it serves does not
        // evaluate it again under the caller being decided.
        return await edge.AsKernel(new BrainId(connectionBrain), async () =>
        {
            // Shared with the app's principal; an app id outside the caller's brain only matches a legacy pairing.
            var connection = neurons.Connection(connectionKey);
            return BrainKey.BelongsTo(app, callerBrain)
                ? await connection.IsSharedWith(AppPrincipal.Id(app))
                : await connection.IsSharedInto(callerBrain, app);
        }).ConfigureAwait(false);
    }
}
