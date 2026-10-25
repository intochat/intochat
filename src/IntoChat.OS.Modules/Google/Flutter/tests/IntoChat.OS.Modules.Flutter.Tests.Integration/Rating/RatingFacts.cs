using IntoChat.OS.Modules.Flutter.Rating;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Rating;

public sealed class RatingFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesMaximumAndValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IRating>("owner/stars").Set(5, 4);
        var state = await brain.Get<IRating>("owner/stars").Read();
        Assert.Equal(5, state.Max);
        Assert.Equal(4, state.Value);
    }
}
