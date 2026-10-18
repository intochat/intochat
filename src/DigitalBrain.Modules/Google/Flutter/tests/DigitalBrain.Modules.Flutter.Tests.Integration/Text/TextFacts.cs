using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Text;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Text;

public sealed class TextFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesMarkdown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IText>("about").Set("hello");
        Assert.Equal("hello", (await brain.Get<IText>("about").Read()).Markdown);
    }
}
