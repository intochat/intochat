using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

// R53/R59: at silo start, before any other startup work reads the kernel, a machine puts the account it serves on
// record at the trust side and registers itself for it: the Open posture's default owner (the same provisioning
// an authorized call would do lazily) or the Secured bootstrap user with its account and default brain. It never
// waits for anyone to sign in, so a fresh store boots.
internal sealed class MachineRegistration(MachineRegistrar registrar, IIdentityClient identity, BrainBirths births,
    IOptions<AuthOptions> auth, TimeProvider clock) : IStartupTask
{
    // Ahead of the Active-stage startup tasks (feature boot, seeders) that may read a brain's kernel.
    public const int Stage = ServiceLifecycleStage.Active - 1;

    public async Task Execute(CancellationToken cancellationToken)
    {
        var options = auth.Value;
        if (options.Posture == IdentityPosture.Open)
        {
            var owner = AccountSession.DefaultLogin;
            await identity.ProvisionOwner(new Member { PrincipalId = owner, AccountId = owner, BrainId = owner, Role = MemberRole.Owner, DisplayName = owner });
            await registrar.Bind(owner);
            return;
        }
        // Basic bootstrap is an explicit owner of one default scope, never a wildcard bypass.
        if (options.Username is not { Length: > 0 } principal || string.IsNullOrEmpty(options.Password)) { return; }
        var now = clock.GetUtcNow();
        await identity.EnsureAccount(new Account { AccountId = principal, OwnerPrincipalId = principal, Name = principal, CreatedAt = now });
        var provision = await identity.EnsureBrain(new Member
        {
            PrincipalId = principal, AccountId = principal, BrainId = AccountSession.DefaultLogin,
            DisplayName = principal, Role = MemberRole.Owner, JoinedAt = now,
        });
        await registrar.Bind(principal);
        await births.Born(provision);
    }
}
