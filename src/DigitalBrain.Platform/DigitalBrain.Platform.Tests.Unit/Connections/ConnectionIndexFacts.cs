using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

public sealed class ConnectionIndexFacts
{
    [Fact]
    public async Task TheIndexListsOnlyItsOwnBrainsKeysOnceEach()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var mine = brain.Grains.GetGrain<IConnectionIndex>("alice-home");
        var theirs = brain.Grains.GetGrain<IConnectionIndex>("bob-home");
        await mine.Track(ConnectionKeys.For("alice-home", "example", "one"));
        await mine.Track(ConnectionKeys.For("alice-home", "example", "one"));
        await theirs.Track(ConnectionKeys.For("bob-home", "example", "one"));
        Assert.Equal([ConnectionKeys.For("alice-home", "example", "one")], await mine.List());
        await mine.Untrack(ConnectionKeys.For("alice-home", "example", "one"));
        Assert.Empty(await mine.List());
        Assert.Single(await theirs.List());
    }

    [Fact]
    public async Task TheIndexRefusesAKeyFromAnotherBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(() => brain.Grains.GetGrain<IConnectionIndex>("alice-home").Track(ConnectionKeys.For("bob-home", "example", "one")));
    }
}
