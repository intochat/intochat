using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Testing.Fakes;

namespace IntoChat.OS.Modules.Microsoft.Playwright.Tests.Unit;

// 06-06d: the plain browser neuron keeps its activation alive with an ITimers keep-alive timer for as long as a desktop
// attachment lasts (the grain delayed its deactivation), and releases it when the session ends.
public sealed class PlaywrightKeepAliveFacts
{
    [Fact]
    public async Task AnAttachedBrowserIsKeptAliveUntilItDetaches()
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, context) = await Browser(new FakeBrowserSessionProvider { HangNavigation = true });
        var id = new string('a', 32);

        await browser.Attach(new(1234, id), ct);

        Assert.Contains("playwright.session", context.Timers.KeepAliveNames);
        Assert.Equal(id, Assert.IsType<BrowserReady>(Assert.Single(context.Published)).SessionId);

        await browser.Detach(id, ct);

        Assert.Empty(context.Timers.KeepAliveNames);
        Assert.Equal(id, Assert.IsType<BrowserUnavailable>(context.Published[^1]).SessionId);
    }

    [Fact]
    public async Task AFailedAttachmentReleasesTheActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var pending = new TaskCompletionSource<IBrowserPageSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (browser, context) = await Browser(new FakeBrowserSessionProvider { Pending = pending });
        pending.SetException(new InvalidOperationException("no page"));

        await Assert.ThrowsAnyAsync<Exception>(() => browser.Attach(new(1234, new string('b', 32)), ct));

        Assert.Empty(context.Timers.KeepAliveNames);
        Assert.IsType<BrowserUnavailable>(Assert.Single(context.Published));
    }

    private static async Task<(PlaywrightNeuron Browser, FakeNeuronContext Context)> Browser(IBrowserSessionProvider provider)
    {
        var browser = new PlaywrightNeuron(provider);
        var context = new FakeNeuronContext(DigitalBrain.NeuronId.Parse("facts/playwright/browser"));
        NeuronAttachment.Attach(browser, context);
        await NeuronAttachment.Activate(browser, CancellationToken.None);
        return (browser, context);
    }
}
