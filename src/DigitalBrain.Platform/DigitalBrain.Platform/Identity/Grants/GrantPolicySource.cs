using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Identity.Grants;

internal interface IGrantPolicySource
{
    ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CallerContext caller, CancellationToken cancellationToken);
}

internal sealed class GrainGrantPolicySource(IIdentityClient identity, IOptions<AuthOptions>? auth = null) : IGrantPolicySource
{
    public ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken)
    {
        var caller = request.Caller;
        if (request.PublicOperation) { return ValueTask.FromResult<CallDecision?>(null); }
        if (caller.Kind == CallerKind.Platform) { return ValueTask.FromResult<CallDecision?>(null); }
        if (auth?.Value.Posture == IdentityPosture.Open && caller.PrincipalId == AccountSession.DefaultLogin
            && caller.AccountId == AccountSession.DefaultLogin && caller.Kind is CallerKind.User or CallerKind.Assistant)
        { return ValueTask.FromResult<CallDecision?>(null); }
        return Decide(request, cancellationToken);
    }

    // Open posture: a brain under the synthetic owner's account belongs to the synthetic owner; a first call into an
    // untouched one provisions it through the machine's signed ProvisionOwner (R59/R68), then decides again.
    private async ValueTask<CallDecision?> Decide(CallRequest request, CancellationToken cancellationToken)
    {
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
                PrincipalId = AccountSession.DefaultLogin, AccountId = AccountSession.DefaultLogin, BrainId = caller.BrainId,
                Role = MemberRole.Owner, DisplayName = AccountSession.DefaultLogin,
            }).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException) { return decision; }
        return await identity.Authorize(request).WaitAsync(cancellationToken);
    }
    public async ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CallerContext caller, CancellationToken cancellationToken)
        => await identity.ListGrants(BrainScope.Create(caller.AccountId, caller.BrainId).Id).WaitAsync(cancellationToken);
}
