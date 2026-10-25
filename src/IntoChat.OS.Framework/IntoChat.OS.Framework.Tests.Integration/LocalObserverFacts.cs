using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class LocalObserverFacts
{
    [Fact]
    public async Task AnObserverFailureIsReportedAndUnsubscribed()
    {
        var hub = new LocalSignalHub(NullLogger<LocalSignalHub>.Instance);
        var id = GrainId.Create("test", "observer");
        var observer = new Observer(_ => Task.FromException(new IOException("Observer failed")));
        hub.Subscribe(id, observer);
        hub.Publish(id, new SignalNudge(new(id.ToString()), 1));
        Assert.IsType<IOException>(await observer.Error.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        hub.Publish(id, new SignalNudge(new(id.ToString()), 2));
        Assert.Equal(1, observer.Deliveries);
    }

    [Fact]
    public async Task ASlowObserverCoalescesNudgesAndStillReadsEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new LocalSignalHub(NullLogger<LocalSignalHub>.Instance);
        var journal = new MemorySignalJournal(Options.Create(new MemorySignalJournalOptions()));
        var id = GrainId.Create("test", "slow");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new List<Signal>();
        var cursor = 0L;
        var observer = new Observer(async _ =>
        {
            await pending.Task;
            var part = (await journal.SignalsOf(id, cursor))!;
            read.AddRange(part.Signals.Select(entry => entry.Signal));
            cursor = part.Last;
        });
        hub.Subscribe(id, observer);

        for (var i = 0; i < 1100; i++)
        { hub.Publish(id, new SignalNudge(new(id.ToString()), await journal.Append(id, [new Number(i)]))); }
        pending.TrySetResult();
        await hub.FlushAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.False(observer.Error.Task.IsCompleted);
        Assert.InRange(observer.Deliveries, 1, 1100 - 1);
        Assert.Equal(Enumerable.Range(0, 1100), read.Cast<Number>().Select(number => number.Value));
    }

    // A flush that began while an observer had nudges queued must not wait on that observer once it leaves:
    // its queued nudges are never delivered, so they never drain.
    [Fact]
    public async Task AFlushIsNotHeldByAnObserverThatLeavesWithNudgesQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new LocalSignalHub(NullLogger<LocalSignalHub>.Instance);
        var id = GrainId.Create("test", "leaving");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer(_ => { entered.TrySetResult(); return held.Task; });
        hub.Subscribe(id, observer);
        hub.Publish(id, new SignalNudge(new(id.ToString()), 1));
        hub.Publish(id, new SignalNudge(new(id.ToString()), 2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var flush = hub.FlushAsync(ct);
        hub.Unsubscribe(id, observer);
        await flush.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(1, observer.Deliveries);
    }

    private sealed class Observer(Func<SignalNudge, Task> receive) : INeuronObserver, ILocalSignalFaultSink
    {
        public int Deliveries { get; private set; }
        public TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Nudge(SignalNudge nudge) { Deliveries++; return receive(nudge); }
        public void OnError(Exception error) => Error.TrySetResult(error);
    }
}
