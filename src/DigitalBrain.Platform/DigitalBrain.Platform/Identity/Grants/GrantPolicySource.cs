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
        return new(identity.Authorize(request).WaitAsync(cancellationToken));
    }
    public async ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CallerContext caller, CancellationToken cancellationToken)
        => await identity.ListGrants(BrainScope.Create(caller.AccountId, caller.BrainId).Id).WaitAsync(cancellationToken);
}
