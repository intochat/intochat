// os-only: a stage of the os call filter, the single enforcement point for CallRequest.
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Modules.Identity.Contracts.Grants;

// One increment of the single enforcement point. A stage returns null to abstain or a decision to
// settle the request. Lower Order runs first: allowance (P3) runs before grants so an app call that
// carries a grant but no allowance is still stopped. Stages with the same Order keep registration
// order. A stage returns null on success so later stages still get to decide.
public interface ICallFilterStage
{
    int Order => 0;

    ValueTask<CallDecision?> EvaluateAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken = default);
}
