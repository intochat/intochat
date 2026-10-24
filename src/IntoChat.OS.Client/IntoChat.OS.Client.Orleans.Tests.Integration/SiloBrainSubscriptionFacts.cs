using Xunit;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

public sealed class SiloBrainSubscriptionFacts(ClientHost host)
{
    // Quarantined (timing): https://github.com/intochat/os/issues/311 - removed by Phase 4
    [Fact(Explicit = true)]
    public async Task SiloHostedBrainClientReceivesPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var listener = brain.Get<ISiloBrainListener>("listener");
        Assert.True(await listener.HubIsPresent());
        await using var heard = await brain.Observe<Heard>(listener, ct);
        await listener.Listen("silo-source");
        await brain.Get<ITestEmitter>("silo-source").Emit(42);
        Assert.Equal(42, (await heard.NextAsync(ct: ct)).Value);
    }

    // Quarantined (timing): https://github.com/intochat/os/issues/311 - removed by Phase 4
    [Fact(Explicit = true)]
    public async Task SiloHostedSubscriptionEndsWhenItsTokenIsCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

        Assert.True(await brain.Get<ISiloBrainListener>("canceling-listener").CancelingTheTokenEndsASubscription("canceled-source"));
    }
}
