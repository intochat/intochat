using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

internal sealed class OwnerBootstrap(IIdentityClient identity, BrainBirths births, IOptions<AuthOptions> options, TimeProvider clock) : IStartupTask
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
