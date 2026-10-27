using IntoChat.OS.Testing.Fakes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Playwright.Tests.Integration;

public sealed class PlaywrightNeuronFacts(PlaywrightHost host)
{
    [Fact]
    [Trait("host", "own")]
    public async Task Starting_after_desktop_attachment_replaces_it_with_a_managed_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true };
        await using var brain = await Start(provider);
        using var caller = brain.Owner.AsCaller();
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        await browser.Start(ct);
        await browser.Attach(new(1234, new string('d', 32)), ct);
        Assert.Equal(new string('d', 32), (await browser.Read()).SessionId);
        await browser.Start(ct);
        Assert.NotEqual(new string('d', 32), (await browser.Read()).SessionId);
        Assert.True(provider.Pages[1].Disposed);
        Assert.Equal(3, provider.Pages.Count);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task Attaching_publishes_ready_from_the_browser_and_deactivation_releases_the_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        await using var ready = await brain.Observe<BrowserReady>(browser, ct);
        await browser.Attach(new(1234, new string('a', 32)), ct);
        var signal = await ready.NextAsync(ct: ct);
        Assert.Equal(new string('a', 32), signal.SessionId);
        Assert.True((await browser.Read()).Ready);
        await brain.DeactivateAsync(browser, ct);
        Assert.True(provider.Pages[0].Disposed);
        Assert.False((await browser.Read()).Connected);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task Disconnect_interrupts_navigation_and_publishes_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        await using var unavailable = await brain.Observe<BrowserUnavailable>(browser, ct);
        var id = new string('a', 32);
        await browser.Attach(new(1234, id), ct);
        var navigation = browser.Navigate("https://example.com", ct);
        await provider.Pages[0].NavigationStarted.Task.WaitAsync(ct);
        await browser.Detach(id, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        Assert.Equal(id, (await unavailable.NextAsync(ct: ct)).SessionId);
        Assert.False((await browser.Read()).Ready);
        Assert.True(provider.Pages[0].Disposed);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task A_second_connect_supersedes_an_attachment_still_in_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true, BlockedSessionId = new string('a', 32) };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        await using var ready = await brain.Observe<BrowserReady>(browser, ct);
        var first = browser.Attach(new(1234, new string('a', 32)), ct);
        await provider.Started.Task.WaitAsync(ct);
        var second = browser.Attach(new(1234, new string('b', 32)), ct);
        await second;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await browser.Detach(new string('a', 32), ct);
        Assert.Equal(new string('b', 32), (await ready.NextAsync(ct: ct)).SessionId);
        Assert.Equal(new string('b', 32), (await browser.Read()).SessionId);
        Assert.Single(ready.Snapshot);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task A_pending_connection_rejects_actions_without_canceling_the_attachment()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        var attach = browser.Attach(new(1234, new string('a', 32)), ct);
        await provider.Started.Task.WaitAsync(ct);
        await Assert.ThrowsAsync<BrowserNotConnectedException>(() => browser.Snapshot(ct));
        Assert.Equal(new string('a', 32), (await browser.Read()).SessionId);
        provider.Release.SetResult();
        await attach;
        Assert.True((await browser.Read()).Ready);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task Disconnect_during_attachment_never_publishes_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true, BlockedSessionId = new string('a', 32) };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        await using var ready = await brain.Observe<BrowserReady>(browser, ct);
        await using var unavailable = await brain.Observe<BrowserUnavailable>(browser, ct);
        var id = new string('a', 32);
        var attach = browser.Attach(new(1234, id), ct);
        await provider.Started.Task.WaitAsync(ct);
        await browser.Detach(id, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
        Assert.Empty(ready.Snapshot);
        Assert.Equal(id, (await unavailable.NextAsync(ct: ct)).SessionId);
        Assert.False((await browser.Read()).Ready);
    }

    [Fact]
    [Trait("host", "own")]
    public async Task Repeated_connect_does_not_interrupt_navigation()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { HangNavigation = true };
        await using var brain = await Start(provider);
        var browser = brain.Get<IPlaywright>(brain.Owner.Key("browser"));
        var attachment = new BrowserAttachment(1234, new string('a', 32));
        await browser.Attach(attachment, ct);
        var navigation = browser.Navigate("https://example.com", ct);
        await provider.Pages[0].NavigationStarted.Task.WaitAsync(ct);
        await browser.Attach(attachment, ct);
        Assert.Single(provider.Pages);
        Assert.False(navigation.IsCompleted);
        await browser.Detach(attachment.SessionId, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
    }

    [Fact]
    public async Task An_unattached_browser_fails_with_a_catchable_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await host.PersonAsync("unattached", ct);
        using var caller = owner.AsCaller();
        var browser = host.Brain.Get<IPlaywright>(owner.Key("browser"));
        await Assert.ThrowsAsync<BrowserNotConnectedException>(() => browser.Navigate("https://example.com", ct));
        await Assert.ThrowsAsync<BrowserNotConnectedException>(() => browser.Snapshot(ct));
    }

    // The browser session host stands in for a CDP-attached page whose attach and navigation timing the facts script.
    private static Task<ITestBrain> Start(FakeBrowserSessionProvider provider) => TestBrain.Create().WithModule<PlaywrightModule>()
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(provider))
        .StartAsync(TestContext.Current.CancellationToken);
}
