using System.Diagnostics;
using System.Runtime.CompilerServices;
using IntoChat.OS.Testing.Aspire;
using Microsoft.Playwright;

namespace IntoChat.OS.Testing.Playwright;

public static class BrainBrowser
{
    // One Chromium per booted AppHost, shared by every lease of it; each session is its own context.
    private static readonly ConditionalWeakTable<AspireTestSession, SessionBrowser> Browsers = new();

    // A fresh browser context on the brain's web endpoint, owned by the brain (a lease releases its own).
    public static Task<BrowserSession> OpenBrowserAsync(this ITestBrain brain, CancellationToken ct)
        => brain.OpenBrowserAsync(new BrowserOptions(), ct);

    public static async Task<BrowserSession> OpenBrowserAsync(this ITestBrain brain, BrowserOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Invalid options fail before the brain is touched or any browser resource is acquired.
        var resolved = BrowserOptionsResolver.Resolve(options, BrowserEnvironment.HeadedRequested, Debugger.IsAttached,
            BrowserEnvironment.LocationRequested);
        var aspire = AspireBrainExtensions.Of(brain);
        var session = aspire.Session;
        var booted = session.BrowserServer is null ? BrowserLocation.Local : BrowserLocation.Container;
        if (options.Location is { } expected && expected != booted)
        { throw new InvalidOperationException($"This brain booted a {booted} browser; start the target with new AspireTarget<TAppHost>(BrowserLocation.{expected})."); }
        if (!resolved.Headless && booted == BrowserLocation.Container)
        { throw new InvalidOperationException("A headed browser runs locally: start the target with new AspireTarget<TAppHost>(BrowserLocation.Local)."); }
        var browser = Browsers.GetValue(session, owner => new SessionBrowser(owner));
        return await browser.OpenAsync(aspire.Lifetime, resolved, ct).ConfigureAwait(false);
    }

    private sealed class SessionBrowser(AspireTestSession session)
    {
        private readonly SemaphoreSlim _gate = new(1);
        private IBrowser? _browser;

        public async Task<BrowserSession> OpenAsync(TestSessionLifetime owner, ResolvedBrowserOptions options, CancellationToken cancellationToken)
        {
            var endpoint = session.BrowserEndpoint ?? throw new InvalidOperationException("This application configuration exposes no browser endpoint.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.StartupTimeout);
            var watch = Stopwatch.StartNew();
            var stage = "browser-lock";
            var acquired = false;
            BrowserSession? opened = null;
            try
            {
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                if (_browser is null)
                {
                    stage = "browser-launch";
                    var playwright = await AcquireAsync(Microsoft.Playwright.Playwright.CreateAsync(),
                        value => { value.Dispose(); return Task.CompletedTask; }, deadline.Token).ConfigureAwait(false);
                    session.Lifetime.Own("playwright", new AsyncAction(() => { playwright.Dispose(); return ValueTask.CompletedTask; }));
                    using var cancelLaunch = deadline.Token.Register(playwright.Dispose);
                    _browser = session.BrowserServer is { } server
                        ? await playwright.Chromium.ConnectAsync(server.ToString(), new()
                        {
                            ExposeNetwork = BrowserContainer.ExposedNetwork,
                            SlowMo = options.SlowMoMilliseconds,
                            Timeout = Remaining(),
                        }).ConfigureAwait(false)
                        : await playwright.Chromium.LaunchAsync(new()
                        {
                            Headless = options.Headless,
                            SlowMo = options.SlowMoMilliseconds,
                            Timeout = Remaining(),
                        }).ConfigureAwait(false);
                    session.Lifetime.Own("browser", _browser);
                }
                deadline.Token.ThrowIfCancellationRequested();
                stage = "browser-context";
                var context = await AcquireAsync(_browser.NewContextAsync(), value => value.CloseAsync(), deadline.Token).ConfigureAwait(false);
                opened = new BrowserSession(context, session.Options.ArtifactDirectory, cancellationToken);
                owner.Own("browser-context", opened);
                using var cancelStartup = deadline.Token.Register(() => BrowserSession.CloseInBackground(context));
                await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true }).ConfigureAwait(false);
                var page = await context.NewPageAsync().ConfigureAwait(false);
                opened.Page = page;
                // The shell fails silently from the test's point of view without this: a browser
                // assertion that times out needs the page's own errors in the failure output.
                page.Console += (_, message) =>
                {
                    if (message.Type is "error" or "warning")
                    { Console.WriteLine($"[shell-console] {message.Type}: {message.Text}"); }
                };
                page.PageError += (_, error) => Console.WriteLine($"[shell-pageerror] {error}");
                page.RequestFailed += (_, request) => Console.WriteLine($"[shell-requestfailed] {request.Method} {request.Url}: {request.Failure}");
                page.Response += (_, response) =>
                {
                    if (response.Status >= 400)
                    { Console.WriteLine($"[shell-response] {response.Status} {response.Request.Method} {response.Url}"); }
                };
                page.SetDefaultTimeout((float)options.AssertionTimeout.TotalMilliseconds);
                // Playwright's Expect() has its own 5s default, separate from the page timeout.
                Assertions.SetDefaultExpectTimeout((float)options.AssertionTimeout.TotalMilliseconds);
                stage = "browser-readiness";
                await PreparePageAsync(page, endpoint, session.BrowserReadySelector, Remaining, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return opened;
            }
            catch (Exception error)
            {
                if (opened is not null)
                {
                    try { await opened.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { error.Data["BrowserCleanupFailure"] = cleanup; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                { throw new TimeoutException($"Browser startup timed out at '{stage}' after {options.StartupTimeout}.", error); }
                throw new InvalidOperationException($"Browser startup failed at '{stage}'.", error);
            }
            finally { if (acquired) { _gate.Release(); } }

            float Remaining()
            {
                deadline.Token.ThrowIfCancellationRequested();
                return (float)Math.Max(1, (options.StartupTimeout - watch.Elapsed).TotalMilliseconds);
            }
        }
    }

    internal static async Task PreparePageAsync(IPage page, Uri endpoint, string? readySelector,
        Func<float> remaining, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await page.GotoAsync(endpoint.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = remaining() }).ConfigureAwait(false);
            if (readySelector is null) { return; }
            try
            {
                await page.Locator(readySelector).First.WaitForAsync(new()
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = Math.Min(8_000, remaining()),
                }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) when (!ct.IsCancellationRequested && remaining() > 2_000)
            {
                // A healthy development server can answer before its first build is available.
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<T> AcquireAsync<T>(Task<T> acquisition, Func<T, Task> release, CancellationToken ct)
    {
        try { return await acquisition.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            // A browser protocol operation may finish after cancellation; do not leak its result.
            _ = acquisition.ContinueWith(async completed =>
            {
                if (completed.IsCompletedSuccessfully)
                {
                    try { await release(completed.Result).ConfigureAwait(false); }
                    catch { /* Best effort after ownership could not be acquired. */ }
                }
                else { _ = completed.Exception; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    private sealed class AsyncAction(Func<ValueTask> action) : IAsyncDisposable
    { public ValueTask DisposeAsync() => action(); }
}
