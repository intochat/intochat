using IntoChat.OS.Modules.Flutter.Text;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Text;

// A migrated control keeps the access rule it had as a grain (06-04, PlainNeuronCallFilter): an edge-stamped caller reaches
// its own brain's controls and is refused another brain's; silo-side calls without a caller pass.
public sealed class PlainControlAccessFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AnEdgeCallerReachesItsOwnBrainsControl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var text = brain.Get<IText>("owner/note");
        using (brain.Owner.AsCaller()) { await text.Set("mine"); }
        Assert.Equal("mine", (await text.Read()).Markdown);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEdgeCallerOfAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var text = brain.Get<IText>("owner/note");
        await text.Set("owner's");
        using (brain.Owner.AsCallerIn("bob"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => text.Set("bob's"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(text.Read);
        }
        Assert.Equal("owner's", (await text.Read()).Markdown);
    }
}
