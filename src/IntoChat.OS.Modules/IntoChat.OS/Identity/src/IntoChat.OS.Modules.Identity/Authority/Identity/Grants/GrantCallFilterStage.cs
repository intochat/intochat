using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Identity.Grants;

internal sealed class GrantCallFilterStage(IGrantPolicySource source) : ICallFilterStage
{
    public ValueTask<CallDecision?> EvaluateAsync(CallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (GrantRules.EvaluateZeroTokenGuard(request) is { } zeroTokenDenial)
        {
            return ValueTask.FromResult<CallDecision?>(zeroTokenDenial);
        }
        return source.AuthorizeAsync(request, cancellationToken);
    }
}
