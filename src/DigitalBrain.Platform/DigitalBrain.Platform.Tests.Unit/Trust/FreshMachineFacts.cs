using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R59: on an empty store nothing has provisioned the default owner yet; the machine does it at boot, before the
// kernel's first read, so the owner's core part opens without any authorized call having run first.
public sealed class FreshMachineFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AFreshMachineProvisionsItsDefaultOwnerAtBoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
        Assert.Null(await trust.Grains.GetGrain<DigitalBrain.Platform.Identity.Authority.IBrainAuthority>(scope).Account());

        await using var machine = await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct);

        Assert.Equal(AccountSession.DefaultLogin, await machine.Services.GetRequiredService<IKeyringClient>().OwnerOf(scope));
        var core = await machine.Services.GetRequiredService<PartLogs>().For(PartId.Core(scope));
        Assert.False(core.Closed);

        // R158: boot records the account too — an account-less store refuses every CreateBrain through the account,
        // which is the first circle or a second personal brain.
        var identity = machine.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>();
        var account = await identity.ReadAccount(AccountSession.DefaultLogin);
        Assert.NotNull(account);
        Assert.Equal(AccountSession.DefaultLogin, account.OwnerPrincipalId);
        var second = await identity.CreateBrain(AccountSession.DefaultLogin, AccountSession.DefaultLogin, "fresh-machine-second-brain", "second", DigitalBrain.Framework.Platform.Identity.BrainKind.Personal);
        Assert.False(string.IsNullOrWhiteSpace(second.Member.BrainId));
    }
}
