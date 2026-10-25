using IntoChat.OS.Modules.Flutter.Text;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Text;

public sealed class TextFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesMarkdown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IText>("owner/about").Set("hello");
        Assert.Equal("hello", (await brain.Get<IText>("owner/about").Read()).Markdown);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ConditionalSetPreservesAStatusWrittenAfterTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var text = brain.Get<IText>("owner/status");
        await text.Set("Temporary connection failure");
        var before = await text.Read();
        await text.Set("A newer warning");
        var newer = await text.Read();

        Assert.False(await text.TrySet("", before.Version));
        var preserved = await text.Read();
        Assert.Equal("A newer warning", preserved.Markdown);
        Assert.Equal(newer.Version, preserved.Version);
        Assert.True(await text.TrySet("Recovered", preserved.Version));
        var recovered = await text.Read();
        Assert.Equal("Recovered", recovered.Markdown);
        Assert.Equal(preserved.Version + 1, recovered.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CompetingConditionalWritersCannotBothReplaceTheSameVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var text = brain.Get<IText>("owner/status");
        var before = await text.Read();

        var applied = await Task.WhenAll(text.TrySet("first", before.Version), text.TrySet("second", before.Version));

        Assert.Single(applied, accepted => accepted);
        var after = await text.Read();
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(applied[0] ? "first" : "second", after.Markdown);
    }
}
