using DigitalBrain;
using DigitalBrain.Testing.Module;
using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Unit;

public sealed class ReactFacts
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RebuildRunsOnceBeforeAnySignalAndOncePerSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var source = brain.Get<ITestEmitter>("react-1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runs = 0;
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var react = program.React<Number>(source, _ =>
        {
            var n = Interlocked.Increment(ref runs);
            if (n == 1) { firstRun.TrySetResult(); }
            if (n == 2) { secondRun.TrySetResult(); }
            return Task.CompletedTask;
        }, stop.Token);
        await firstRun.Task.WaitAsync(Wait, ct);
        Assert.Equal(1, runs);
        await source.Emit(1);
        await secondRun.Task.WaitAsync(Wait, ct);
        Assert.Equal(2, runs);
        await stop.CancelAsync();
        await react.WaitAsync(Wait, ct); // cancellation ends React quietly
    }

    [Fact]
    public async Task SignalsDuringARebuildCoalesceIntoOneFollowingRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var source = brain.Get<ITestEmitter>("react-2");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runs = 0;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var react = program.React<Number>(source, async _ =>
        {
            var n = Interlocked.Increment(ref runs);
            if (n == 2) { holding.TrySetResult(); await release.Task; }
            if (n == 3) { third.TrySetResult(); }
        }, stop.Token);
        await Task.Delay(300, ct); // first rebuild done, subscription registered
        await source.Emit(1);
        await holding.Task.WaitAsync(Wait, ct); // second rebuild is running
        await source.Emit(2);
        await source.Emit(3);
        await source.Emit(4);
        release.SetResult();
        await third.Task.WaitAsync(Wait, ct);
        await Task.Delay(500, ct);
        Assert.Equal(3, runs); // three signals during the hold became one rebuild
        await stop.CancelAsync();
        await react.WaitAsync(Wait, ct);
    }

    [Fact]
    public async Task AFailingRebuildEndsReactWithThatError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var source = brain.Get<ITestEmitter>("react-3");
        var runs = 0;
        var react = program.React<Number>(source, _ => Interlocked.Increment(ref runs) == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask, ct);
        await Task.Delay(300, ct);
        await source.Emit(1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => react.WaitAsync(Wait, ct));
        Assert.Equal("boom", error.Message);
    }
}
