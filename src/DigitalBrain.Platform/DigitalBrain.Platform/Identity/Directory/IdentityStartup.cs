using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Directory;

internal sealed class IdentityStartup(IIdentityClient identity, BrainBirths births, IOptions<AuthOptions> auth, TimeProvider clock, DeploymentStorageIdentity storageIdentity) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        await storageIdentity.BindAsync(cancellationToken);
        // Basic bootstrap is an explicit owner of one default scope, never a wildcard bypass.
        if (auth.Value.Username is not { Length: > 0 } principal || string.IsNullOrEmpty(auth.Value.Password)) { return; }
        var now = clock.GetUtcNow();
        await identity.EnsureAccount(new Account { AccountId = principal, OwnerPrincipalId = principal, Name = principal, CreatedAt = now });
        await births.Born(await identity.EnsureBrain(new Member
        {
            PrincipalId = principal,
            AccountId = principal,
            BrainId = AccountSession.DefaultLogin,
            DisplayName = principal,
            Role = MemberRole.Owner,
            JoinedAt = now
        }));
    }
}
