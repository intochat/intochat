using Microsoft.Extensions.Options;

using MicrosoftSdk = Microsoft;
namespace IntoChat.OS.Modules.Microsoft.Playwright.Tests.Browser;

public sealed class ManagedBrowserFacts
{
    [Fact]
    public async Task A_managed_neuron_starts_idempotently_and_refuses_another_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PlaywrightModule>().StartAsync(ct);
        var alice = await brain.PersonAsync("managed-alice", ct);
        var bob = await brain.PersonAsync("managed-bob", ct);
        var browser = brain.Get<IPlaywright>(alice.Key("browser"));
        using (alice.AsCaller())
        {
            await browser.Start(ct);
            var initial = await browser.Read();
            Assert.True(initial.Ready);
            await browser.Start(ct);
            Assert.Equal(initial.SessionId, (await browser.Read()).SessionId);
        }
        using (bob.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(browser.Read);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => browser.Stop(ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => browser.Start(ct));
        }
        using (alice.AsCaller())
        {
            Assert.True((await browser.Read()).Ready);
            await browser.Stop(ct);
            await browser.Stop(ct);
            Assert.False((await browser.Read()).Connected);
            await browser.Start(ct);
            Assert.True((await browser.Read()).Ready);
            await brain.DeactivateAsync(browser, ct);
            Assert.False((await browser.Read()).Connected);
        }
    }

    [Fact]
    public async Task Managed_browser_reads_public_web_content_through_the_network_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider();
        await using var page = await provider.StartAsync("account-public", "a", ct);
        var observation = await page.NavigateAsync("https://example.com", ct);
        Assert.Equal("Example Domain", observation.Title);
        Assert.Contains("documentation", observation.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://example.com/", observation.Url);
    }

    [Fact]
    public async Task Managed_pages_are_ephemeral_and_stop_does_not_close_another_accounts_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider();
        await using var first = await provider.StartAsync("account-a", "a", ct);
        await using var second = await provider.StartAsync("account-b", "b", ct);
        Assert.True(first.Connected);
        Assert.True(second.Connected);
        await first.DisposeAsync();
        Assert.False(first.Connected);
        Assert.True(second.Connected);
        Assert.StartsWith("about:blank", (await second.SnapshotAsync(ct)).Url);
    }

    [Fact]
    public async Task Managed_pages_reject_private_navigation_and_deployment_evaluation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider(Options.Create(new PlaywrightModuleOptions
        { AppOrigins = ["http://localhost:5080"] }));
        await using var page = await provider.StartAsync("account-a", "a", ct);
        await Assert.ThrowsAsync<ArgumentException>(() => page.NavigateAsync("http://localhost:5080", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => page.EvaluateAsync("1+1", ct));
    }

    [Fact]
    public async Task A_worker_crash_disconnects_only_that_accounts_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider();
        await using var first = await provider.StartAsync("account-a", "a", ct);
        await using var sibling = await provider.StartAsync("account-a", "b", ct);
        await using var other = await provider.StartAsync("account-b", "c", ct);
        // Fault injection at the process owner: closing the launched browser terminates its Chromium process.
        var workers = (System.Collections.IDictionary)typeof(PlaywrightSessionProvider)
            .GetField("_workers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(provider)!;
        var worker = workers["account-a"]!;
        var browser = (MicrosoftSdk.Playwright.IBrowser)worker.GetType().GetProperty("Browser")!.GetValue(worker)!;
        await browser.CloseAsync();
        Assert.False(first.Connected);
        Assert.False(sibling.Connected);
        Assert.True(other.Connected);
        Assert.StartsWith("about:blank", (await other.SnapshotAsync(ct)).Url);
    }

    [Fact]
    public async Task Cancelling_an_action_closes_only_its_ephemeral_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider();
        await using var page = await provider.StartAsync("account-a", "a", ct);
        await using var sibling = await provider.StartAsync("account-a", "b", ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = page.ClickAsync("#absent", cancel.Token);
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await page.DisposeAsync();
        Assert.False(page.Connected);
        Assert.True(sibling.Connected);
    }

    [Fact]
    public async Task Idle_sessions_and_host_shutdown_release_browser_processes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider(Options.Create(new PlaywrightModuleOptions
        { ManagedSessionIdleTimeout = TimeSpan.FromSeconds(1), MaxManagedAccounts = 1 }));
        await using var idle = await provider.StartAsync("account-a", "a", ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.False(idle.Connected);
        await using var replacement = await provider.StartAsync("account-b", "b", ct);
        Assert.True(replacement.Connected);
        await provider.DisposeAsync();
        Assert.False(replacement.Connected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.StartAsync("account-c", "c", ct));
    }

    [Fact]
    public async Task Cancelled_start_does_not_consume_worker_capacity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new PlaywrightSessionProvider(Options.Create(new PlaywrightModuleOptions
        { MaxManagedAccounts = 1, MaxManagedSessionsPerAccount = 1 }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.StartAsync("cancelled", "a", cancelled.Token));
        await using var page = await provider.StartAsync("account-a", "a", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync("account-b", "b", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync("account-a", "b", ct));
        await page.DisposeAsync();
        await using var replacement = await provider.StartAsync("account-b", "b", ct);
        Assert.True(replacement.Connected);
    }
}
