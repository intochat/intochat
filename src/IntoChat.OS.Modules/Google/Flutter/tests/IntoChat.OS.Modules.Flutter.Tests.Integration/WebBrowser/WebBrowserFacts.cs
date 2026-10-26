using IntoChat.OS.Testing.Fakes;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Modules.Flutter.WebBrowser.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Modules.Microsoft.Playwright;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.WebBrowser;

public sealed class WebBrowserFacts
{
    // The shell's own pages (localhost:181xx) are exempt from the public-only navigation rule.
    private const string AppPages = "http://localhost:181";

    private const string Session = "0123456789abcdef0123456789abcdef";
    private static Task<ITestBrain> Start(FakeBrowserSessionProvider provider) => TestBrain.Create().WithModule<FlutterModule>()
        .WithModule<PlaywrightModule>()
        // The shell's CDP page session: the debugging port lives on the user's device, so the fake stands in for the attachment.
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(provider))
        .StartAsync(TestContext.Current.CancellationToken);

    // The shell's edge reaches the browser grain through its connector interface, which is not a neuron contract.
    private static IWebBrowserConnector Connector(ITestBrain brain, string name) => brain.Get<IWebBrowserConnector>(name);

    [Trait("host", "own")]
    [Fact]
    public async Task Connecting_the_shell_attaches_the_declared_driver_and_publishes_ready_without_a_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        var handler = brain.Get<IPrimitiveTestHandler>("research/browser-handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await using var connected = await brain.Observe<BrowserConnected>(browser, ct);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connector.Connect(80, Session));
        await Assert.ThrowsAsync<ArgumentException>(() => connector.Connect(12345, "invalid"));
        await connector.Connect(12345, Session);
        Assert.Equal(new BrowserAttachment(12345, Session), Assert.Single(provider.Attachments));
        Assert.True((await brain.Get<IPlaywright>("research/driver").Read()).Connected);
        Assert.False((await brain.Get<IPlaywright>("research/browser").Read()).Connected);
        Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
        Assert.Empty((await browser.Read()).Error);
        var signal = await connected.NextAsync(ct: ct);
        Assert.Equal("research/browser", signal.Name);
        Assert.Equal(Session, signal.SessionId);
        Assert.Equal(new BrowserConnected("research/browser", Session), Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected))));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Disconnecting_detaches_the_driver_and_publishes_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        var connector = Connector(brain, "research/browser");
        await using var disconnected = await brain.Observe<BrowserDisconnected>(browser, ct);
        await connector.Connect(12345, Session);
        await connector.Disconnect(Session);
        Assert.False((await brain.Get<IPlaywright>("research/browser").Read()).Connected);
        Assert.True(Assert.Single(provider.Pages).Disposed);
        Assert.Equal(Session, (await disconnected.NextAsync(ct: ct)).SessionId);
        await connector.Disconnect(Session);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Failed_detachment_does_not_announce_disconnection_and_can_be_retried()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        var handler = brain.Get<IPrimitiveTestHandler>("research/handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await connector.Connect(12345, Session);
        var page = Assert.Single(provider.Pages);
        page.FailDispose = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.Disconnect(Session));

        Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
        Assert.IsType<BrowserConnected>(Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected))));
        page.FailDispose = false;
        await connector.Disconnect(Session);
        Assert.Equal(BrowserConnection.Waiting, (await browser.Read()).Connection);
        Assert.Single((await Lifecycle(handler, handled, nameof(BrowserDisconnected))).OfType<BrowserDisconnected>());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task A_pending_detach_cannot_announce_disconnection_or_overwrite_a_replacement_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        var handler = brain.Get<IPrimitiveTestHandler>("research/handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await connector.Connect(12345, Session);
        var page = Assert.Single(provider.Pages);
        page.PendingDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = connector.Disconnect(Session);
        await page.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        try
        {
            Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
            Assert.IsType<BrowserConnected>(Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected))));
            var repeatedDisconnect = connector.Disconnect(Session);
            await browser.Read();
            Assert.False(repeatedDisconnect.IsCompleted);
            var replacement = connector.Connect(12345, new string('b', 32));
            // Read is a scheduling barrier on the reentrant browser grain.
            await browser.Read();
            page.PendingDispose.SetResult();
            await Task.WhenAll(disconnect, repeatedDisconnect, replacement).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
            Assert.DoesNotContain(await Lifecycle(handler), signal => signal is BrowserDisconnected);
            Assert.True((await brain.Get<IPlaywright>("research/driver").Read()).Ready);
        }
        finally { page.PendingDispose.TrySetResult(); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Deactivation_detaches_without_changing_status_or_dispatching_ui_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var handler = brain.Get<IPrimitiveTestHandler>("research/handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await Connector(brain, "research/browser").Connect(12345, Session);
        await brain.Get<IText>("research/status").Set("Researching...");

        await brain.DeactivateAsync(browser, ct);

        Assert.True(Assert.Single(provider.Pages).Disposed);
        Assert.Equal("Researching...", (await brain.Get<IText>("research/status").Read()).Markdown);
        Assert.IsType<BrowserConnected>(Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected))));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Failed_cleanup_during_deactivation_does_not_dispatch_a_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var handler = brain.Get<IPrimitiveTestHandler>("research/handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await Connector(brain, "research/browser").Connect(12345, Session);
        Assert.Single(provider.Pages).FailDispose = true;

        await brain.DeactivateAsync(browser, ct);

        Assert.Equal(BrowserConnection.Waiting, (await browser.Read()).Connection);
        Assert.IsType<BrowserConnected>(Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected))));
        await browser.Read();
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Recomposition_and_rebinding_do_not_replace_a_live_session_or_reset_observation()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        var connector = Connector(brain, "research/browser");
        var handler = brain.Get<IPrimitiveTestHandler>("research/handler");
        await browser.Configure("research/driver");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        await connector.Connect(12345, Session);
        await browser.Configure("research/driver");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await connector.Connect(12345, Session);
        Assert.Single(provider.Attachments);
        Assert.Single(await Lifecycle(handler, handled, nameof(BrowserConnected)));
        Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.Configure("other/driver"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Two_windows_sharing_a_debugging_port_drive_distinct_grains_without_crosstalk()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        await brain.Get<IWebBrowser>("workspace/first/browser").Configure("workspace/first/driver");
        await brain.Get<IWebBrowser>("workspace/second/browser").Configure("workspace/second/driver");
        var first = Connector(brain, "workspace/first/browser");
        var second = Connector(brain, "workspace/second/browser");
        var secondSession = new string('b', 32);
        await first.Connect(12345, Session);
        await second.Connect(12345, secondSession);
        await first.Disconnect(Session);
        Assert.False((await brain.Get<IPlaywright>("workspace/first/driver").Read()).Connected);
        Assert.Equal(secondSession, (await brain.Get<IPlaywright>("workspace/second/driver").Read()).SessionId);
        Assert.True((await brain.Get<IPlaywright>("workspace/second/driver").Read()).Connected);
        Assert.False(provider.Pages[1].Disposed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task A_stale_disconnect_cannot_detach_a_replacement_session()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var connector = Connector(brain, "research/browser");
        await connector.Connect(12345, Session);
        await connector.Connect(12345, new string('b', 32));
        await connector.Disconnect(Session);
        Assert.True((await brain.Get<IPlaywright>("research/browser").Read()).Connected);
        Assert.True(provider.Pages[0].Disposed);
        Assert.False(provider.Pages[1].Disposed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARestartedBrowserIsWaitingNotReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        await connector.Connect(12345, Session);
        await brain.DeactivateAsync(browser, ct);
        Assert.False((await brain.Get<IPlaywright>("research/driver").Read()).Connected);
        var restarted = await browser.Read();
        Assert.Equal(BrowserConnection.Waiting, restarted.Connection);
        Assert.Empty(restarted.Error);
        Assert.Empty(restarted.ActualUrl);
        await connector.Connect(12345, new string('b', 32));
        Assert.True((await brain.Get<IPlaywright>("research/driver").Read()).Connected);
        Assert.Equal(0, (await browser.Read()).Version);
        Assert.Empty((await browser.Read()).Uri);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Disconnecting_during_attachment_prevents_a_late_ready_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages, Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var brain = await Start(provider);
        await brain.Get<IWebBrowser>("research/browser").Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        var connect = connector.Connect(12345, Session);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await using var unavailable = await brain.Observe<BrowserUnavailable>(brain.Get<IPlaywright>("research/driver"), ct);
        var disconnect = connector.Disconnect(Session);
        Assert.Equal(Session, (await unavailable.NextAsync(ct: ct)).SessionId);
        Assert.Null((await brain.Get<IPlaywright>("research/driver").Read()).SessionId);
        var page = new FakeBrowserPageSession(provider);
        provider.Pending.SetResult(page);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await disconnect.WaitAsync(ct);
        Assert.True(page.Disposed);
        Assert.False((await brain.Get<IPlaywright>("research/driver").Read()).Connected);
        Assert.Equal(BrowserConnection.Waiting, (await brain.Get<IWebBrowser>("research/browser").Read()).Connection);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Failed_attachment_reports_unavailable_and_can_be_retried()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages, Fail = true };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        await browser.Configure("research/driver");
        var connector = Connector(brain, "research/browser");
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.Connect(12345, Session));
        var failed = await browser.Read();
        Assert.Equal(BrowserConnection.Unavailable, failed.Connection);
        Assert.Equal("Browser unavailable", failed.Error);
        Assert.False((await brain.Get<IPlaywright>("research/driver").Read()).Ready);
        provider.Fail = false;
        await connector.Connect(12345, Session);
        Assert.Equal(BrowserConnection.Attached, (await browser.Read()).Connection);
    }

    [Fact]
    public void The_script_contract_and_status_signal_do_not_expose_the_debugging_port()
    {
        // The connector is the shell's contract, kept out of the contracts assembly that scripts compile against.
        Assert.NotEqual(typeof(IWebBrowser).Assembly, typeof(IWebBrowserConnector).Assembly);
        Assert.DoesNotContain(typeof(IWebBrowser).GetMethods(), method => method.Name is "Connect" or "Disconnect" or "Unavailable");
        Assert.DoesNotContain(typeof(BrowserConnected).GetProperties(), property => property.Name == "Port");
        Assert.DoesNotContain(typeof(WebBrowserState).GetProperties(), property => property.Name == "Port");
        Assert.DoesNotContain(typeof(WebBrowserView).GetProperties(), property => property.Name == "Port");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Navigating_publishes_the_uri()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var browser = brain.Get<IWebBrowser>("research/docs");
        await using var nav = await brain.Observe<BrowserNavigated>(browser, ct);
        await browser.Navigate("https://learn.microsoft.com/winui", "WinUI");
        Assert.Equal("https://learn.microsoft.com/winui", (await nav.NextAsync(ct: ct)).Uri);
        Assert.Equal("WinUI", (await browser.Read()).Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAttachedPageShowsTheBrowserUri()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("film/browser");
        await browser.Configure("film/browser");
        await browser.Navigate("http://localhost:18111/", "FilmCraft");
        await Connector(brain, "film/browser").Connect(12345, Session);
        var page = Assert.Single(provider.Pages);
        Assert.Equal(["http://localhost:18111/"], page.Navigations);
        var attached = await browser.Read();
        Assert.Equal(BrowserConnection.Attached, attached.Connection);
        Assert.Equal("http://localhost:18111/", attached.ActualUrl);
        await browser.Navigate("http://localhost:18110/", "PhotoCraft");
        Assert.Equal(["http://localhost:18111/", "http://localhost:18110/"], page.Navigations);
        Assert.Equal("http://localhost:18110/", (await browser.Read()).ActualUrl);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnavailableShellBrowserReportsItsReason()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("research/browser");
        var connector = Connector(brain, "research/browser");
        var handler = brain.Get<IPrimitiveTestHandler>("research/unavailable-handler");
        await brain.Get<IUiBinding>("research/browser").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, TestContext.Current.CancellationToken);
        const string reason = "The WebView2 environment could not be started.";
        await connector.Unavailable(reason);
        var view = await browser.Read();
        Assert.Equal(BrowserConnection.Unavailable, view.Connection);
        Assert.Equal(reason, view.Error);
        Assert.Empty(view.ActualUrl);
        // The binding dispatches to the handler asynchronously: wait for the handler to record it.
        await PrimitiveBindingFacts.WaitFor(handled, 1, TestContext.Current.CancellationToken);
        Assert.Contains((await handler.Events()).Decoded(), signal => signal is BrowserObservationChanged);
        await connector.Unavailable(new string('x', 300));
        Assert.Equal(200, (await browser.Read()).Error.Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ANavigationFailureIsTheBrowsersError()
    {
        var provider = new FakeBrowserSessionProvider { AppOrigin = AppPages };
        await using var brain = await Start(provider);
        var browser = brain.Get<IWebBrowser>("film/browser");
        await browser.Configure("film/browser");
        await Connector(brain, "film/browser").Connect(12345, Session);
        var page = Assert.Single(provider.Pages);
        page.FailNavigate = true;
        await browser.Navigate("http://localhost:18110/");
        var failed = await browser.Read();
        Assert.Equal(BrowserConnection.Failed, failed.Connection);
        Assert.Equal("Browser could not load http://localhost:18110/: Load rejected", failed.Error);
        page.FailNavigate = false;
        await browser.Navigate("http://localhost:18110/");
        var recovered = await browser.Read();
        Assert.Equal(BrowserConnection.Attached, recovered.Connection);
        Assert.Empty(recovered.Error);
        Assert.Equal("http://localhost:18110/", recovered.ActualUrl);
    }

    // Observation changes re-render the shell; these facts are about the session lifecycle.
    private static async Task<object[]> Lifecycle(IPrimitiveTestHandler handler)
        => [.. (await handler.Events()).Decoded().Where(signal => signal is not BrowserObservationChanged)];

    // The binding dispatches to the handler asynchronously: wait until it has recorded the expected lifecycle signal.
    private static async Task<object[]> Lifecycle(IPrimitiveTestHandler handler, SignalProbe<UiEventHandled> handled, string expected)
    {
        while ((await handled.NextAsync(ct: TestContext.Current.CancellationToken)).Type != expected) { }
        return await Lifecycle(handler);
    }
}
