using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Tests.Browser.Scenarios.WarmBrowserFixture))]

namespace IntoChat.OS.Tests.Browser.Scenarios;

/// <summary>
/// Single shared warm Playwright driver and Chromium browser instance for the entire
/// <c>IntoChat.OS.Tests.Browser</c> assembly. Each test opens an isolated <see cref="IBrowserContext"/>
/// (&lt;20ms) instead of spawning a new Chromium process per test.
/// </summary>
public sealed class WarmBrowserFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _debugPort;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async Task<IBrowserContext> NewContextAsync(BrowserNewContextOptions? options = null, CancellationToken ct = default)
    {
        var browser = await EnsureBrowserAsync(ct).ConfigureAwait(false);
        return await browser.NewContextAsync(options).WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<IBrowserContext> NewContextAsync(int width, int height, CancellationToken ct = default)
        => NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = width, Height = height } }, ct);

    /// <summary>
    /// Opens an isolated context and marked blank page (<c>about:blank#intochat-os-&lt;id&gt;</c>)
    /// inside the shared CDP-enabled Chromium process so <c>BrowserSessionOwner</c> tests can attach
    /// without cold-booting a dedicated browser process per fact.
    /// </summary>
    public async Task<DebuggablePage> OpenDebuggablePageAsync(CancellationToken ct = default)
    {
        var browser = await EnsureBrowserAsync(ct).ConfigureAwait(false);
        var sessionId = Guid.NewGuid().ToString("N");
        var context = await browser.NewContextAsync().WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
            await page.GotoAsync($"about:blank#intochat-os-{sessionId}").WaitAsync(ct).ConfigureAwait(false);
            return new DebuggablePage(context, _debugPort, sessionId);
        }
        catch
        {
            await context.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true } existing)
            {
                return existing;
            }

            if (_browser is not null)
            {
                try { await _browser.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* best effort teardown of disconnected browser before relaunch */ }
                _browser = null;
            }

            _playwright ??= await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);

            var port = FreePort();
            var launchOptions = BrowserLaunch.Options(Environment.GetEnvironmentVariable);
            var existingArgs = launchOptions.Args ?? [];
            launchOptions.Args = [.. existingArgs, $"--remote-debugging-port={port}"];

            _browser = await _playwright.Chromium.LaunchAsync(launchOptions).WaitAsync(ct).ConfigureAwait(false);
            _debugPort = port;
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                try { await _browser.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* best effort shutdown */ }
                _browser = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    public sealed class DebuggablePage(IBrowserContext context, int port, string sessionId) : IAsyncDisposable
    {
        public int Port => port;
        public string SessionId => sessionId;

        public async ValueTask DisposeAsync()
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
    }
}
