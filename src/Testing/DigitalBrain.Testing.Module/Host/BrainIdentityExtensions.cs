using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Testing.Module;

public static class BrainIdentityExtensions
{
    public static Task AuthorizeCallerAsync(this ITestBrain brain)
    {
        var caller = CallerContextStamper.Require();
        return brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainAuthority>(BrainScope.CurrentId()).ProvisionOwner(new Member
        {
            PrincipalId = caller.PrincipalId,
            AccountId = caller.AccountId,
            BrainId = caller.BrainId,
            Role = MemberRole.Owner,
            DisplayName = caller.PrincipalId,
        });
    }

    // A brain born on this machine for an account other than the default owner: its owner on record at the
    // trust side and the machine bound to the account (R55), as a brain birth leaves them, without the neuron.
    public static async Task OwnBrainAsync(this ITestBrain brain, string accountId, string brainId, string? principalId = null)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var principal = principalId ?? accountId;
        await brain.Services.GetRequiredService<IIdentityClient>().ProvisionOwner(new Member
        {
            PrincipalId = principal,
            AccountId = accountId,
            BrainId = brainId,
            Role = MemberRole.Owner,
            DisplayName = principal,
        });
        await brain.Services.GetRequiredService<MachineRegistrar>().Bind(accountId);
    }
}
