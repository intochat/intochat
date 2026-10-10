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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => b.SiloServices.GetRequiredService<GrainTrustClients>().CompleteRegistration("bob"));
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

    [Fact(Timeout = 60_000)]
    // R72: every real machine carries a pairing hash (configured or its own); this drives the trust gate directly with a
    // machine registered without one.
    public async Task AMachineWithoutAPairingHashCannotRegisterAUsername()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var machine = SequencerFacts.Machine();
        await TrustClientFacts.Register(brain.SiloServices.GetRequiredService<INodeDirectoryClient>(), machine, "mallory", []);
        var unpaired = GrainTrustClients.WithScratchCounter(brain.Grains, machine);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unpaired.RegisterPrincipal("eve", "Eve"));
        Assert.Null(await brain.SiloServices.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().RegisteredBy("eve"));
    }
}
