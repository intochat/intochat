using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Button;

public sealed class ButtonFacts
{
    [Fact]
    public async Task ClickPublishesAction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var button = brain.Get<IButton>("go");
        await using var clicks = await brain.Observe<ButtonClicked>(button, ct);
        await button.Set("Open", "navigate:docs");
        await button.Click();
        var first = await clicks.NextAsync(ct: ct);
        Assert.Equal("navigate:docs", first.Action);
        Assert.NotEqual(Guid.Empty, first.RequestId);
        await button.Click();
        var second = await clicks.NextAsync(ct: ct);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(2, (await button.Read()).ClickCount);
    }
}
