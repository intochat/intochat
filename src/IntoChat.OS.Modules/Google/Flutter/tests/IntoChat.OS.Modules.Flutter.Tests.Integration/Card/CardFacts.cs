using IntoChat.OS.Modules.Flutter.Card;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Card;

public sealed class CardFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesTitleBodyAndChildren()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<ICard>("hero").Set("Title", "Body", [new UiChildRef("button", "go")]);
        var state = await brain.Get<ICard>("hero").Read();
        Assert.Equal("Title", state.Title);
        Assert.Equal("Body", state.Body);
        Assert.Equal(new UiChildRef("button", "go"), Assert.Single(state.Children));
    }
}
