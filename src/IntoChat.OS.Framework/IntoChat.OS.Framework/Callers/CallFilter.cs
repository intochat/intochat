
namespace IntoChat.OS.Framework;

// The one enforcement point. It first validates that the caller carries a trusted-edge stamp,
// then lets each registered stage decide. Stages are ordered increments: grants now, allowances
// and limits later, then the broker gateway. The first non-null decision wins; an abstaining
// pipeline allows.
// R134/R146: a cross-brain call is denied before any stage, except an app's use of another brain's connection through
// a binding recorded when the app was shared there.
public sealed class CallFilter(IEnumerable<ICallFilterStage> stages, ICallBindings? bindings = null) : ICallFilter
{
    private readonly IReadOnlyList<ICallFilterStage> _stages = [.. stages.OrderBy(stage => stage.Order)];

    public async ValueTask<CallDecision> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller;
        if (string.IsNullOrWhiteSpace(caller.PrincipalId))
        {
            return CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no principal.");
        }

        if (!CallerContextStamper.IsTrusted(caller))
        {
            return CallDecision.Deny(CallDenial.UntrustedCaller, "The principal was not stamped at a trusted edge.");
        }

        if (CallerContextStamper.TryGet(out var ambient) && ambient != caller)
        {
            return CallDecision.Deny(CallDenial.UntrustedCaller, "The principal does not match the trusted edge stamp.");
        }

        var callerScope = BrainScope.Create(caller.AccountId, caller.BrainId).Id;
        if (request.EnforceTarget && caller.Kind != CallerKind.Platform && !request.PublicOperation
            && request.TargetScope != callerScope && !await BoundConnection(request, callerScope, cancellationToken).ConfigureAwait(false))
        {
            return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target operation is not available in the selected brain.");
        }

        foreach (var stage in _stages)
        {
            var decision = await stage.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            if (decision is not null)
            {
                return decision;
            }
        }

        return CallDecision.Allow();
    }

    // An app behavior calling into another brain through a recorded binding; the target's own rules still refuse management.
    private async ValueTask<bool> BoundConnection(CallRequest request, string callerScope, CancellationToken cancellationToken)
    {
        if (bindings is null || request.Caller.Kind != CallerKind.App || request.Caller.AppId is not { Length: > 0 }) { return false; }
        return await bindings.IsBound(request, callerScope, cancellationToken).ConfigureAwait(false);
    }
}
