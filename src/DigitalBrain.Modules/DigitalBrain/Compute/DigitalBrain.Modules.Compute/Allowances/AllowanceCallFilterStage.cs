using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Compute.Allowances;

// P3's allowance increment of the one call filter. It runs before the grant stage (Order -1) so an
// app call that has a grant but no allowance is still stopped. On an allowed call it abstains
// (returns null) so the remaining stages still decide; only a denial settles the pipeline.
internal sealed class AllowanceCallFilterStage(IAllowancePolicySource source) : ICallFilterStage
{
    public int Order => -1;

    public async ValueTask<CallDecision?> EvaluateAsync(CallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mapped = new AllowanceRequest(request.Caller.AccountId, request.Caller.BrainId, request.Caller.AppId, IntentId: null,
            (AllowanceCallerKind)(int)request.Caller.Kind, request.TargetNeuron, request.Operation, request.SemanticTypeIds, request.EstimatedCompute, request.HasSideEffects,
            request.Caller.ConversationId);
        var decision = await source.AuthorizeAsync(mapped, cancellationToken).ConfigureAwait(false);
        if (decision.Allowed) { return null; }
        return CallDecision.Deny(
            decision.Denial is { } denial ? (CallDenial)(int)denial : CallDenial.MissingAllowance,
            decision.Explanation ?? "No allowance covers this call.");
    }
}
