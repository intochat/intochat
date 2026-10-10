using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R34: a machine of a separate trust cluster reaches it through a private gateway client; what it writes lands in
// the trust cluster, and its own silo hosts no trust grain.
public sealed class TrustGatewayFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AMachineReachesASeparateTrustClusterThroughAPrivateGatewayClient()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await ModuleTest.Create().WithTrust(trust).StartAsync(ct);
        using var signer = SequencerFacts.Machine();
        var owner = DigitalBrain.Framework.BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;

        var sequencer = machine.SiloServices.GetRequiredService<ISequencerClient>();
        Assert.True(await sequencer.CompareAndSet("p-gateway", 0, SequencerFacts.Record(signer, "p-gateway", 1, TimeSpan.FromMinutes(1))));
        Assert.Equal(1, (await trust.Grains.GetGrain<ISequencerGrain>("p-gateway").Get())!.Version);
        Assert.Equal(ModuleTest.DefaultAccount, await machine.SiloServices.GetRequiredService<IKeyringClient>().OwnerOf(owner));
        Assert.NotNull(await machine.SiloServices.GetRequiredService<IIdentityClient>().Membership(owner, ModuleTest.DefaultAccount));

        Assert.NotSame(machine.SiloServices.GetRequiredService<IGrainFactory>(), machine.SiloServices.GetRequiredService<TrustGatewayClient>().Grains);
        await Assert.ThrowsAnyAsync<Exception>(() => machine.Grains.GetGrain<ISequencerGrain>("p-gateway").Get());
    }
}
