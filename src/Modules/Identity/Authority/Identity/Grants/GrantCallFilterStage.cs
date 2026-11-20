using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Identity.Grants;

internal sealed class GrantCallFilterStage(IGrantPolicySource source) : ICallFilterStage
{
    public ValueTask<CallDecision?> EvaluateAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (GrantRules.EvaluateZeroTokenGuard(caller, request) is { } zeroTokenDenial)
        {
            return ValueTask.FromResult<CallDecision?>(zeroTokenDenial);
        }
        return source.AuthorizeAsync(caller, request, cancellationToken);
    }
}
