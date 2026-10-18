using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Integration;

public sealed class SiloBrainSubscriptionFacts(ClientHost host)
{
    [Fact]
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

    [Fact]
    public async Task SiloHostedSubscriptionEndsWhenItsTokenIsCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

        Assert.True(await brain.Get<ISiloBrainListener>("canceling-listener").CancelingTheTokenEndsASubscription("canceled-source"));
    }
}
