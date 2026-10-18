using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

public sealed class SubscriptionLifetimeFacts(ClientHost host)
{
    // A hub delivery can nudge a subscription that host shutdown has already disposed.
    [Fact]
    public async Task ANudgeAfterDisposalNeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("late-nudge");
        var stream = await brain.SubscribeAsync<Number>(source, ct);
        await stream.DisposeAsync();
        await ((INeuronObserver)stream).Nudge(new SignalNudge(source.GetGrainId().ToString(), long.MaxValue));
        await stream.DisposeAsync();
        Assert.True(stream.Completion.IsCompletedSuccessfully);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OverflowIsVisibleEvenWithoutDrainingTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.Create(1).StartAsync(ct);
        var source = brain.Get<ITestEmitter>("overflow");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await source.Emit(1);
        await source.Emit(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.Completion.WaitAsync(TimeSpan.FromSeconds(3), ct));
        await stream.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.Completion);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, reader.Current.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task AFailingReadDoesNotEndTheSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<IFlakyEmitter>("flaky");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await source.FailNextRead();
        await source.Emit(1);
        await source.Emit(2);

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(1, reader.Current.Value);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(2, reader.Current.Value);
        Assert.False(stream.Completion.IsCompleted);
    }

    [Fact]
    public async Task CancellationEndsAnIdleSubscriptionAndDisposalIsIdempotent()
    {
        var brain = host.Brain;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = brain.Get<ITestEmitter>("cancel");
        var stream = await brain.SubscribeAsync<Number>(source, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => brain.SubscribeAsync<Number>(source, cancel.Token));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RenewalKeepsAHealthySubscriptionAliveBeyondItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.StartAsync(ct);
        var source = brain.Get<ITestEmitter>("renew");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await source.Emit(19);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct));
        Assert.Equal(19, reader.Current.Value);
    }

    // A reactivated source has forgotten its watchers, so the publish nudges nobody; the next renewal
    // re-watches and pulls what was journalled in the gap.
    [Trait("host", "own")]
    [Fact]
    public async Task AMissedNudgeIsRecoveredOnTheNextRenewal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.StartAsync(ct);
        var source = brain.Get<ITestEmitter>("missed");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await source.Deactivate();
        await source.Emit(23);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(23, reader.Current.Value);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASignalPublishedDuringSubscribeIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.Create().ConfigureServices(services => services.AddSingleton<ISignalJournal, EagerJournal>()).StartAsync(ct);
        await using var stream = await brain.SubscribeAsync<Number>(brain.Get<IEagerEmitter>("eager"), ct);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct));
        Assert.Equal(7, reader.Current.Value);
    }

    [Fact]
    public async Task FinishingTwiceNeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var stream = await brain.SubscribeAsync<Number>(brain.Get<ITestEmitter>("finish"), ct);
        var sink = Assert.IsAssignableFrom<ILocalSignalFaultSink>(stream);
        sink.OnError(new InvalidOperationException("first"));
        sink.OnError(new InvalidOperationException("second"));
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal("first", (await Assert.ThrowsAsync<InvalidOperationException>(() => stream.Completion)).Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OnClosesItsSubscriptionWhenTheConsumerCancelsMidStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.StartAsync(ct);
        var source = brain.Get<ITestEmitter>("on-cancel");
        var client = brain.Services.GetRequiredService<BrainClient>();
        using var consumer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in brain.On<Number>(source, () => { registered.TrySetResult(); return Task.CompletedTask; }, consumer.Token)) { }
        }, ct);
        await registered.Task.WaitAsync(ct);
        Assert.Equal(1, client.TrackedSubscriptions);
        consumer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, client.TrackedSubscriptions);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DisposingAnOnEnumeratorWhileAReadIsInFlightLeavesNoSubscriptionAndThrowsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await FastBrain.StartAsync(ct);
        var source = brain.Get<ITestEmitter>("in-flight");
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerator = brain.On<Number>(source, () => { registered.TrySetResult(); return Task.CompletedTask; }, ct).GetAsyncEnumerator(ct);
        var pending = enumerator.MoveNextAsync();
        await registered.Task.WaitAsync(ct);
        await enumerator.DisposeAsync();
        try { await pending; } catch (OperationCanceledException) { }
        Assert.Equal(0, brain.Services.GetRequiredService<BrainClient>().TrackedSubscriptions);
    }
}
