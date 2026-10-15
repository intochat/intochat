using DigitalBrain.Platform.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class HostedWorkspaceRequestLifetimeFacts
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveRequestStopsAtRevocationOrOriginalGrantExpiry(bool revoke)
    {
        var directory = Directory.CreateTempSubdirectory("workspace-stream-").FullName;
        var clock = new FakeTimeProvider();
        var options = Options.Create(new HostedRuntimeOptions { StateDirectory = directory, AccountId = "a", BrainId = "b", RuntimeId = "r" });
        var store = new HostedWorkspaceStore(options, clock);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var issued = await store.IssueAsync("user", "session", ct);
            var grant = Assert.IsType<HostedWorkspaceStore.Grant>(await store.ExchangeAsync(issued.Code, ct));
            // Starting a request near expiry must not extend the grant's lifetime.
            clock.Advance(TimeSpan.FromMinutes(4));
            var context = new DefaultHttpContext();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = HostedWorkspaceRequestLifetime.RunAsync(context, grant.Id, grant.ExpiresAt, store, clock, async http =>
            {
                started.TrySetResult();
                try { while (true) { await Task.Delay(10, http.RequestAborted); } }
                catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (revoke) { await store.RevokeSessionAsync("session", ct); }
            clock.Advance(revoke ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(1));
            await running.WaitAsync(TimeSpan.FromSeconds(3), ct);
            Assert.True(context.RequestAborted.IsCancellationRequested);
        }
        finally { Directory.Delete(directory, true); }
    }
}
