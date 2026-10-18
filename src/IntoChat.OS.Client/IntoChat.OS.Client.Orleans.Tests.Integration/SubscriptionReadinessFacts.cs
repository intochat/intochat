using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

public sealed class SubscriptionReadinessFacts(ClientHost host)
{
    [Fact]
    public async Task RecoveryCanPublishTheFirstSignalAfterRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("ready-publication");
        var callbacks = 0;
        await using var reader = brain.On<Number>(source, async () =>
        {
            callbacks++;
            await source.Emit(42);
        }, ct).GetAsyncEnumerator(ct);

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(42, reader.Current.Value);
        await source.Emit(43);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(43, reader.Current.Value);
        Assert.Equal(1, callbacks);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailedRecoveryReleasesItsRegisteredWatchOnDisposal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var source = brain.Get<ITestEmitter>("failed-recovery");
        var client = brain.Services.GetRequiredService<BrainClient>();
        var failure = new InvalidOperationException("Recovery failed");
        var reader = brain.On<Number>(source, () => Task.FromException(failure), ct).GetAsyncEnumerator(ct);
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => reader.MoveNextAsync().AsTask()));
        }
        finally { await reader.DisposeAsync(); }
        Assert.Equal(0, client.TrackedSubscriptions);
    }
}
