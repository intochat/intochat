using DigitalBrain;
using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Integration;

public sealed class ReactFacts(ClientHost host)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Counts rebuilds; Run(n) completes when the n-th rebuild starts. The first rebuild runs once the
    // subscription is registered, so Run(1) is the gate before publishing.
    private sealed class Rebuilds(Func<int, Task>? body = null)
    {
        private readonly TaskCompletionSource[] _runs = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task Run(int n) => _runs[n - 1].Task.WaitAsync(Wait);

        public Task Rebuild(CancellationToken _)
        {
            var n = Interlocked.Increment(ref _count);
            if (n <= _runs.Length) { _runs[n - 1].TrySetResult(); }
            return body?.Invoke(n) ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RebuildRunsOnceBeforeAnySignalAndOncePerSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("react-1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rebuilds = new Rebuilds();
        var react = brain.React<Number>(source, rebuilds.Rebuild, stop.Token);
        await rebuilds.Run(1);
        Assert.Equal(1, rebuilds.Count);
        await source.Emit(1);
        await rebuilds.Run(2);
        Assert.Equal(2, rebuilds.Count);
        await stop.CancelAsync();
        await react.WaitAsync(Wait, ct); // cancellation ends React quietly
    }

    [Fact]
    public async Task SignalsDuringARebuildCoalesceIntoOneFollowingRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("react-2");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilds = new Rebuilds(n => n == 2 ? release.Task : Task.CompletedTask);
        var react = brain.React<Number>(source, rebuilds.Rebuild, stop.Token);
        await rebuilds.Run(1);
        await source.Emit(1);
        await rebuilds.Run(2); // the second rebuild is held
        await source.Emit(2);
        await source.Emit(3);
        await source.Emit(4);
        release.SetResult();
        await rebuilds.Run(3);
        await Task.Delay(500, ct);
        Assert.Equal(3, rebuilds.Count); // three signals during the hold became one rebuild
        await stop.CancelAsync();
        await react.WaitAsync(Wait, ct);
    }

    [Fact]
    public async Task AFailingRebuildEndsReactWithThatError()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("react-3");
        var rebuilds = new Rebuilds(n => n == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        var react = brain.React<Number>(source, rebuilds.Rebuild, ct);
        await rebuilds.Run(1);
        await source.Emit(1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => react.WaitAsync(Wait, ct));
        Assert.Equal("boom", error.Message);
    }
}
