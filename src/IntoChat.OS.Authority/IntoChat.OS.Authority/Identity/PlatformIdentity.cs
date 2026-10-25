using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Authority.Identity.Grants;

namespace IntoChat.OS.Authority.Identity;

internal sealed class PlatformIdentity(IBrainAccess access, IGrantPolicySource grants) : IIdentity
{
    public CallerContext? CurrentPrincipal => CallerContextStamper.TryGet(out var caller) ? caller : null;

    public string RequirePrincipal() => CallerContextStamper.Require().PrincipalId;

    public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => access.CanAccessAsync(principalId, accountId, brainId, cancellationToken);

    public async ValueTask<bool> HasGrantAsync(CallerContext caller, string semanticTypeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticTypeId);
        return GrantRules.IsGranted(caller, semanticTypeId, await grants.ListGrantsAsync(caller, cancellationToken));
    }
}
