using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Testing.Module;

// Swept in Phase 4: callers move to PersonAsync/AsCaller. Both go through the machine's signed
// IIdentityClient.ProvisionOwner, which binds this machine to the account first (R63), as a brain birth does.
public static class BrainIdentityExtensions
{
    public static Task AuthorizeCallerAsync(this ITestBrain brain)
    {
        var caller = CallerContextStamper.Require();
        return ProvisionOwnerAsync(brain, caller.PrincipalId, caller.AccountId, caller.BrainId);
    }

    // A brain born on this machine for an account other than the default owner: its owner on record at the
    // trust side and the machine bound to the account (R55), as a brain birth leaves them, without the neuron.
    public static Task OwnBrainAsync(this ITestBrain brain, string accountId, string brainId, string? principalId = null)
        => ProvisionOwnerAsync(brain, principalId ?? accountId, accountId, brainId);

    private static Task ProvisionOwnerAsync(ITestBrain brain, string principal, string accountId, string brainId)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain.Services.GetRequiredService<IIdentityClient>().ProvisionOwner(new Member
        {
            PrincipalId = principal,
            AccountId = accountId,
            BrainId = brainId,
            Role = MemberRole.Owner,
            DisplayName = principal,
        });
    }
}
