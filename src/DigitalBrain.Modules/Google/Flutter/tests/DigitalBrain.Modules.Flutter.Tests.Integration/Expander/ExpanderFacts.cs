using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Expander;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Expander;

public sealed class ExpanderFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetThenToggle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var expander = brain.Get<IExpander>("more");
        await expander.Set("More", false, []);
        await expander.Toggle();
        Assert.True((await expander.Read()).Expanded);
    }
}
