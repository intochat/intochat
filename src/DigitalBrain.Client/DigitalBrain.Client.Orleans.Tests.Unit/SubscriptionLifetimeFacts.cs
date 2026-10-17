using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Unit;

public sealed class SubscriptionLifetimeFacts
{
    private static TestBrain Options(int capacity = 256) => TestBrain.Create().WithFastSubscriptions(capacity);

    [Fact]
    public async Task OverflowIsVisibleEvenWithoutDrainingTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options(1).StartAsync(ct);
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
    public async Task CancellationEndsAnIdleSubscriptionAndDisposalIsIdempotent()
    {
        await using var brain = await Options().StartAsync(TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = brain.Get<ITestEmitter>("cancel");
        var stream = await brain.SubscribeAsync<Number>(source, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => brain.SubscribeAsync<Number>(source, cancel.Token));
    }

    [Fact]
    public async Task RenewalKeepsAHealthySubscriptionAliveBeyondItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options().StartAsync(ct);
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
    [Fact]
    public async Task AMissedNudgeIsRecoveredOnTheNextRenewal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options().StartAsync(ct);
        var source = brain.Get<ITestEmitter>("missed");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await source.Deactivate();
        await source.Emit(23);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(23, reader.Current.Value);
    }

    [Fact]
    public async Task FinishingTwiceNeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options().StartAsync(ct);
        var stream = await brain.SubscribeAsync<Number>(brain.Get<ITestEmitter>("finish"), ct);
        var sink = Assert.IsAssignableFrom<ILocalSignalFaultSink>(stream);
        sink.OnError(new InvalidOperationException("first"));
        sink.OnError(new InvalidOperationException("second"));
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal("first", (await Assert.ThrowsAsync<InvalidOperationException>(() => stream.Completion)).Message);
    }

    [Fact]
    public async Task OnClosesItsSubscriptionWhenTheConsumerCancelsMidStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options().StartAsync(ct);
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

    [Fact]
    public async Task DisposingAnOnEnumeratorWhileAReadIsInFlightLeavesNoSubscriptionAndThrowsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Options().StartAsync(ct);
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
