using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.Grants;

internal interface IGrantPolicySource
{
    ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CapabilityCaller caller, CancellationToken cancellationToken);
}

internal sealed class GrainGrantPolicySource(IIdentityClient identity, AuthorityMirror mirror, IOptions<AuthOptions>? auth = null) : IGrantPolicySource
{
    public ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken)
    {
        var caller = request.Caller;
        if (request.PublicOperation) { return ValueTask.FromResult<CallDecision?>(null); }
        if (caller.Kind == CallerKind.Platform) { return ValueTask.FromResult<CallDecision?>(null); }
        if (auth?.Value.Posture == IdentityPosture.Open && caller.PrincipalId == AccountSession.DefaultLogin
            && caller.AccountId == AccountSession.DefaultLogin && caller.Kind is CallerKind.User or CallerKind.Agent)
        { return ValueTask.FromResult<CallDecision?>(null); }
        return Decide(request, cancellationToken);
    }

    // Open posture: a brain under the synthetic owner's account belongs to the synthetic owner; a first call into an
    // untouched one provisions it through the machine's signed ProvisionOwner (R59/R68), then decides again.
    private async ValueTask<CallDecision?> Decide(CallRequest request, CancellationToken cancellationToken)
    {
        var (decided, mirrored) = await mirror.TryDecide(request).WaitAsync(cancellationToken);
        if (decided && !Provisions(request, mirrored)) { return mirrored; }
        var decision = await identity.Authorize(request).WaitAsync(cancellationToken);
        var caller = request.Caller;
        if (decision is not { Denial: CallDenial.OutsideWorkspace } || auth?.Value.Posture != IdentityPosture.Open
            || caller.AccountId != AccountSession.DefaultLogin
            || await identity.Membership(BrainScope.Create(caller.AccountId, caller.BrainId).Id, AccountSession.DefaultLogin).WaitAsync(cancellationToken) is not null)
        { return decision; }
        try
        {
            await identity.ProvisionOwner(new Member
            {
                PrincipalId = AccountSession.DefaultLogin,
                AccountId = AccountSession.DefaultLogin,
                BrainId = caller.BrainId,
                Role = MemberRole.Owner,
                DisplayName = AccountSession.DefaultLogin,
            }).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException) { return decision; }
        return await identity.Authorize(request).WaitAsync(cancellationToken);
    }

    // An untouched brain under the open owner is provisioned through the grain. A mirrored denial of any
    // other kind is final: the grain would answer the same way.
    private bool Provisions(CallRequest request, CallDecision? decision)
        => decision is { Denial: CallDenial.OutsideWorkspace }
            && auth?.Value.Posture == IdentityPosture.Open
            && request.Caller.AccountId == AccountSession.DefaultLogin;
    public async ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CapabilityCaller caller, CancellationToken cancellationToken)
        => await identity.ListGrants(BrainScope.Create(caller.AccountId, caller.BrainId).Id).WaitAsync(cancellationToken);
}
