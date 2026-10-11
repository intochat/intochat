using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Clock;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Clock;

public sealed class ClockFacts
{
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
