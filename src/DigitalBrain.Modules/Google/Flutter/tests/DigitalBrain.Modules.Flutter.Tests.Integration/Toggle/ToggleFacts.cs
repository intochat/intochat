using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Toggle;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Toggle;

public sealed class ToggleFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetAndFlipUpdateOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var toggle = brain.Get<IToggle>("dark");
        await toggle.Set("Dark", true);
        Assert.True((await toggle.Read()).On);
        await toggle.Flip();
        Assert.False((await toggle.Read()).On);
    }
}
