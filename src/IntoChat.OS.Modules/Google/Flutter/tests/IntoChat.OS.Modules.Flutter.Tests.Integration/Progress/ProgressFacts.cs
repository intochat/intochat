using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Progress;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Progress;

public sealed class ProgressFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesDeterminacyValueAndLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IProgress>("load").Set(true, 0.4, "loading");
        var state = await brain.Get<IProgress>("load").Read();
        Assert.True(state.Determinate);
        Assert.Equal(0.4, state.Value);
        Assert.Equal("loading", state.Label);
    }
}
