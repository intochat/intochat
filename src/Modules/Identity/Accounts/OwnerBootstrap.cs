using DigitalBrain.Capabilities.Identity;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// Runs on every start so an upgraded home (such as a 0.11 Open-posture brain with no members) gains the Owner membership row idempotently.
internal sealed class OwnerBootstrap(IIdentityClient identity, IBrainBirths births, IOptions<AuthOptions> options, TimeProvider clock) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        var auth = options.Value;
        var principal = auth.Posture == IdentityPosture.Open ? AccountSession.DefaultLogin : auth.Username;
        if (string.IsNullOrWhiteSpace(principal) || (auth.Posture != IdentityPosture.Open && string.IsNullOrEmpty(auth.Password))) { return; }
        await identity.EnsureAccount(new Account { AccountId = principal, OwnerPrincipalId = principal, Name = principal, CreatedAt = clock.GetUtcNow() });
        var provision = await identity.EnsureBrain(new Member
        {
            PrincipalId = principal,
            AccountId = principal,
            BrainId = AccountSession.DefaultLogin,
            Role = MemberRole.Owner,
            DisplayName = principal,
            JoinedAt = clock.GetUtcNow(),
        });
        await births.Born(provision);
    }
}
