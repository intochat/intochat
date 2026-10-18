using Microsoft.Playwright;
using IntoChat.OS.Testing.Aspire.Diagnostics;

namespace IntoChat.OS.Kernel.Testing;

// A per-test account on a collection-owned host. State belongs to that account;
// there is no deployment-global shell document to reset.
public abstract class E2ETest(ReferenceBrainFixture host) : IAsyncLifetime
{
    private ITestBrain? _brain;
    private Person? _person;
    private readonly List<BrowserSession> _browsers = [];
    protected ReferenceBrainFixture Host { get; } = host;
    protected ITestBrain Brain => _brain ?? throw new InvalidOperationException("The fixture has not started.");
    protected Person User => _person ?? throw new InvalidOperationException("The user has not registered.");
    protected string WorkspaceId => User.BrainId;
    public TestTelemetry Telemetry => Brain.Telemetry();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _brain = await Host.LeaseAsync(ct);
        _person = await Brain.PersonAsync("fact-" + Guid.NewGuid().ToString("N"), ct);
    }

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        try
        {
            foreach (var browser in _browsers)
            {
                try { await browser.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            }
            if (_brain is not null && _person is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await AppTestCleanup.RetireAsync(_brain, _person, _person.BrainId, cleanup.Token); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        finally
        {
            _person?.Dispose();
            if (_brain is not null) { await _brain.DisposeAsync(); }
        }
        if (errors.Count > 0) { throw new AggregateException("Test workload cleanup failed.", errors); }
    }

    protected async Task<IPage> OpenPageAsync(CancellationToken cancellationToken)
    {
        var browser = await Brain.OpenBrowserAsync(new BrowserOptions { Headless = true, AssertionTimeout = TimeSpan.FromSeconds(15) }, cancellationToken);
        _browsers.Add(browser);
        await SignInAsync(browser.Page, Brain, User, cancellationToken);
        return browser.Page;
    }

    public static async Task SignInAsync(IPage page, ITestBrain brain, Person person, CancellationToken ct)
    {
        var runtime = new UriBuilder(brain.HttpClient.BaseAddress!) { Host = new Uri(page.Url).Host }.Uri;
        var login = await page.Context.APIRequest.PostAsync(new Uri(runtime, "/identity/login").AbsoluteUri,
            new() { DataObject = new { principalId = person.PrincipalId, password = person.PrincipalId + "-password-123" } }).WaitAsync(ct);
        try { Assert.True(login.Ok, $"Browser login returned {login.Status}."); }
        finally { await login.DisposeAsync(); }
        await page.ReloadAsync().WaitAsync(ct);
    }
}

