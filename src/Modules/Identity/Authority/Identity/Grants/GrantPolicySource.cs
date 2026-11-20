using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.Grants;

internal interface IGrantPolicySource
{
    ValueTask<CallDecision?> AuthorizeAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken);
}

internal sealed class GrainGrantPolicySource(IIdentityClient identity, AuthorityMirror mirror, IOptions<AuthOptions>? auth = null) : IGrantPolicySource
{
    public ValueTask<CallDecision?> AuthorizeAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PublicOperation)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (caller is null)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."));
        }

        if (caller.Actor is Actor.Kernel)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (caller.Actor is Actor.Module)
        {
            return string.IsNullOrEmpty(request.TargetScope)
                || string.Equals(request.TargetScope, caller.Brain.Value, StringComparison.Ordinal)
                ? ValueTask.FromResult<CallDecision?>(CallDecision.Allow())
                : ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.OutsideWorkspace, "A module caller may not cross into another brain."));
        }

        if (auth?.Value.Posture == IdentityPosture.Open
            && caller.Actor is Actor.Person person
            && person.Principal == AccountSession.DefaultLogin
            && person.Account == AccountSession.DefaultLogin)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        return Decide(caller, request, cancellationToken);
    }

    // Open posture: a brain under the synthetic owner's account belongs to the synthetic owner; a first call into an
    // untouched one provisions it through the machine's signed ProvisionOwner (R59/R68), then decides again.
    private async ValueTask<CallDecision?> Decide(Caller caller, CallRequest request, CancellationToken cancellationToken)
    {
        var (decided, mirrored) = await mirror.TryDecide(caller, request).WaitAsync(cancellationToken);
        if (decided && !Provisions(caller, mirrored)) { return mirrored; }
        var decision = await identity.Authorize(caller, request).WaitAsync(cancellationToken);
        if (decision is not { Denial: CallDenial.OutsideWorkspace } || !ActsForOpenOwner(caller)
            || await identity.Membership(caller.Brain.Value, AccountSession.DefaultLogin).WaitAsync(cancellationToken) is not null)
        { return decision; }
        try
        {
            await identity.ProvisionOwner(new Member
            {
                PrincipalId = AccountSession.DefaultLogin,
                AccountId = AccountSession.DefaultLogin,
                BrainId = caller.Brain.Value,
                Role = MemberRole.Owner,
                DisplayName = AccountSession.DefaultLogin,
            }).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException) { return decision; }
        return await identity.Authorize(caller, request).WaitAsync(cancellationToken);
    }

    // An untouched brain under the open owner is provisioned through the grain. A mirrored denial of any
    // other kind is final: the grain would answer the same way.
    private bool Provisions(Caller caller, CallDecision? decision)
        => decision is { Denial: CallDenial.OutsideWorkspace }
            && ActsForOpenOwner(caller);

    // Open posture only: the call is for a person under the synthetic owner's account.
    private bool ActsForOpenOwner(Caller caller)
        => auth?.Value.Posture == IdentityPosture.Open && caller.For?.Account == AccountSession.DefaultLogin;
}
