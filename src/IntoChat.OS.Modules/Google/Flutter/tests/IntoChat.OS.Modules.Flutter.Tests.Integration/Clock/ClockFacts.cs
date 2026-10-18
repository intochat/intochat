using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Clock;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Clock;

public sealed class ClockFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesLabelAndDueDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IClock>("tea").Set("Tea", DateTimeOffset.UnixEpoch);
        var state = await brain.Get<IClock>("tea").Read();
        Assert.Equal("Tea", state.Label);
        Assert.Equal(DateTimeOffset.UnixEpoch, state.DueAt);
    }
}
