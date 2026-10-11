using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R67: a username belongs to the machine that registered it. Another machine can neither complete that
// registration nor sign the principal in, and a refused registration leaves no credential behind.
public sealed class RegistrationTrustFacts
{
    [Fact(Timeout = 120_000)]
    public async Task ARegistrationStartedOnOneMachineCannotBeCompletedOnAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).StartAsync(ct);
        await a.SiloServices.GetRequiredService<PrincipalPasswords>().Register("bob", "bobs-long-password", "Bob");

        var onB = b.SiloServices.GetRequiredService<PrincipalPasswords>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => onB.Register("bob", "attackers-password", "Bob"));
        Assert.Null(await onB.Authenticate("bob", "attackers-password"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => b.SiloServices.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().CompleteRegistration("bob"));
        Assert.NotNull(await a.SiloServices.GetRequiredService<PrincipalPasswords>().Authenticate("bob", "bobs-long-password"));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFailedRegistrationLeavesNoLocalCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).StartAsync(ct);
        await a.SiloServices.GetRequiredService<PrincipalPasswords>().Register("bob", "bobs-long-password", "Bob");

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SiloServices.GetRequiredService<PrincipalPasswords>().Register("bob", "attackers-password", "Bob"));

        Assert.Null(await b.Grains.GetGrain<IPrincipalCredential>("bob").Hash());
    }
}
