using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Hosted;

internal sealed class HostedBrainAccess(IBrainAccess inner, HostedRuntimeOptions scope) : IBrainAccess
{
    public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => accountId == scope.AccountId && brainId == scope.BrainId && principalId == scope.AccountId
            ? inner.CanAccessAsync(principalId, accountId, brainId, cancellationToken)
            : ValueTask.FromResult(false);
}
