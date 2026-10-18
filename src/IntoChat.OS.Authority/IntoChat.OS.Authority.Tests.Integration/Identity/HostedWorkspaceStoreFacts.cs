using IntoChat.OS.Authority.Hosted;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class HostedWorkspaceStoreFacts
{
    [Fact]
    public async Task ExchangeIsOneTimeDurableAndRevocable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "intochat-workspace-test-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new HostedRuntimeOptions { StateDirectory = directory, AccountId = "account", BrainId = "brain", RuntimeId = "runtime" });
        var clock = new FakeTimeProvider();
        var first = new HostedWorkspaceStore(options, clock);
        var issued = await first.IssueAsync("cloud-user", "cloud-session", TestContext.Current.CancellationToken);
        var recovered = new HostedWorkspaceStore(options, clock);
        var consumed = await recovered.ExchangeAsync(issued.Code, TestContext.Current.CancellationToken);
        Assert.NotNull(consumed);
        Assert.Null(await recovered.ExchangeAsync(issued.Code, TestContext.Current.CancellationToken));
        Assert.True(await recovered.IsActiveAsync(consumed.Id, TestContext.Current.CancellationToken));
        await recovered.RevokeSessionAsync("cloud-session", TestContext.Current.CancellationToken);
        Assert.False(await recovered.IsActiveAsync(consumed.Id, TestContext.Current.CancellationToken));
        var expired = await recovered.IssueAsync("cloud-user", "other-session", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await recovered.ExchangeAsync(expired.Code, TestContext.Current.CancellationToken));
        var racing = await recovered.IssueAsync("cloud-user", "race-session", TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(first.ExchangeAsync(racing.Code, TestContext.Current.CancellationToken),
            recovered.ExchangeAsync(racing.Code, TestContext.Current.CancellationToken));
        Assert.Single(outcomes, grant => grant is not null);
        var old = await recovered.IssueAsync("cloud-user", "old-runtime-session", TestContext.Current.CancellationToken);
        var different = new HostedWorkspaceStore(Options.Create(new HostedRuntimeOptions
        { StateDirectory = directory, AccountId = "account", BrainId = "brain", RuntimeId = "different-runtime" }), clock);
        Assert.Null(await different.ExchangeAsync(old.Code, TestContext.Current.CancellationToken));
        Directory.Delete(directory, true);
    }
}
