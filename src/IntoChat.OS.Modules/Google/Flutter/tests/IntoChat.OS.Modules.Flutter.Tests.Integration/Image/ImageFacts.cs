using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Image;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Image;

public sealed class ImageFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesUrlMediaTypeAndPrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IImage>("logo").Set("https://example.com/a.png", "image/png", "logo");
        var state = await brain.Get<IImage>("logo").Read();
        Assert.Equal("https://example.com/a.png", state.Url);
        Assert.Equal("image/png", state.MediaType);
        Assert.Equal("logo", state.Prompt);
    }
}
