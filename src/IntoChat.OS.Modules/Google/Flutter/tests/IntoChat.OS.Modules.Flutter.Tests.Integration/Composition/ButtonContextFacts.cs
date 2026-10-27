using IntoChat.OS.Modules.Flutter.Button;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class ButtonContextFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task DelayedClickCannotInvokeAReplacementAction()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var button = brain.Get<IButton>("owner/a/apps/workspace/close");
        await button.Set("Close", "close:first");
        await button.Set("Close", "close:second");
        await Assert.ThrowsAsync<InvalidOperationException>(() => button.ClickCurrent("close:first"));
        Assert.Equal(0, (await button.Read()).ClickCount);
        await button.ClickCurrent("close:second");
        Assert.Equal(1, (await button.Read()).ClickCount);
    }
}
