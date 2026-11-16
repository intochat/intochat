using DigitalBrain.Access;
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
        ArgumentNullException.ThrowIfNull(request);
        if (request.PublicOperation)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (request.Caller is null)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."));
        }

        if (request.Caller.Actor is Actor.Kernel)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (request.Caller.Actor is Actor.Module)
        {
            return string.IsNullOrEmpty(request.TargetScope)
                || string.Equals(request.TargetScope, request.Caller.Brain.Value, StringComparison.Ordinal)
                ? ValueTask.FromResult<CallDecision?>(CallDecision.Allow())
                : ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.OutsideWorkspace, "A module caller may not cross into another brain."));
        }

        if (auth?.Value.Posture == IdentityPosture.Open
            && request.Caller.Actor is Actor.Person person
            && person.Principal == AccountSession.DefaultLogin
            && person.Account == AccountSession.DefaultLogin)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        return Decide(request, cancellationToken);
    }

    // Open posture: a brain under the synthetic owner's account belongs to the synthetic owner; a first call into an
    // untouched one provisions it through the machine's signed ProvisionOwner (R59/R68), then decides again.
    private async ValueTask<CallDecision?> Decide(CallRequest request, CancellationToken cancellationToken)
    {
        var (decided, mirrored) = await mirror.TryDecide(request).WaitAsync(cancellationToken);
        if (decided && !Provisions(request, mirrored)) { return mirrored; }
        var decision = await identity.Authorize(request).WaitAsync(cancellationToken);
        if (request.Caller is null) { return decision; }
        var caller = CallerIdentity.From(request.Caller);
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
            && request.Caller is not null
            && CallerIdentity.From(request.Caller).AccountId == AccountSession.DefaultLogin;
    public async ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CapabilityCaller caller, CancellationToken cancellationToken)
        => await identity.ListGrants(BrainScope.Create(caller.AccountId, caller.BrainId).Id).WaitAsync(cancellationToken);
}
