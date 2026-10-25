using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Button;

public sealed class ButtonFacts(FlutterHost host)
{
    [Fact]
    public async Task ArtworkDoesNotChangeTheBoundActionAndInvalidImagesAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var button = brain.Get<IButton>("artwork");
        await button.Set("PhotoCraft", "open");
        var visual = new ControlVisual(Svg: "<svg/>", Compact: true, Selected: true);
        await button.SetVisual(visual);
        Assert.Equal(visual, (await button.Read()).Visual);
        Assert.Equal("open", (await button.Read()).Action);
        await Assert.ThrowsAsync<ArgumentException>(() => button.SetVisual(new(ImageUrl: "javascript:alert(1)")));
        await Assert.ThrowsAsync<ArgumentException>(() => button.SetVisual(new(Svg: new string('x', 32001))));
        Assert.Equal(visual, (await button.Read()).Visual);
    }

    [Trait("host", "own")]
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
