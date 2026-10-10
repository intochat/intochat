using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

public sealed class PartKeyFacts
{
    [Fact(Timeout = 60_000)]
    public async Task APartKeyIsCreatedOnceAndStoredOnlyWrapped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var keys = brain.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.Core("home");

        var first = await keys.Key(part, "alice", ct);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, await keys.Key(part, "alice", ct));
        var stored = await brain.Grains.GetGrain<IKeyringGrain>("home").Get(part.Value);
        Assert.NotNull(stored);
        Assert.True(stored.AsSpan().IndexOf(first) < 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task EachPartHasItsOwnKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var keys = brain.SiloServices.GetRequiredService<IPartKeys>();

        Assert.NotEqual(await keys.Key(PartId.Core("home"), "alice", ct), await keys.Key(PartId.ForApp("home", "filmcraft"), "alice", ct));
    }
}
