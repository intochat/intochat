using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Runtime.InMemory.Tests.Unit;

// The in-memory half of HangingWatchFacts (its Orleans half is Framework.Tests.Integration/Runtime/HangingWatchFacts).
// A watch that never answers holds only the watcher's lane: other neurons and the source's interleaving reads go
// on, the watcher's caller times out at its own timeout, and the watcher answers again once the source does.
public sealed class HangingWatchFacts
{
    [Fact]
    public async Task ASourceWhoseWatchHangsHoldsOnlyTheWatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var source = $"brain-1/source/hanging-{Guid.NewGuid():N}";
        var watcher = runtime.ResolveKey<ISourceWatcher>("brain-1/watcher");
        var pinger = runtime.ResolveKey<IPinger>("brain-1/pinger");
        var hanging = runtime.ResolveKey<IHangingSource>(source);
        HangingSource.Arm(source);

        var watch = watcher.WatchSource(source);
        await HangingSource.Entered(source).WaitAsync(ct);

        Assert.Equal(1, await pinger.Ping().WaitAsync(ct));
        Assert.Empty((await hanging.Read(0, 10).WaitAsync(ct)).Signals);
        var queued = watcher.Pings();
        // The queued call is blocked on the watcher's turn, not merely unscheduled.
        await runtime.Waiting<ISourceWatcher>("brain-1/watcher").WaitAsync(ct);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => watch.WaitAsync(ct));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(queued.IsCompleted);

        HangingSource.Release(source);
        Assert.Equal(0, await queued.WaitAsync(ct));
    }
}
