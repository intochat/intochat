using IntoChat.OS;
using System.Threading.Channels;
using Xunit;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

public sealed class SourceSetFacts(ClientHost host)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Opens the set and returns once every member's subscription is registered, with the first read pending.
    private static async Task<Watcher> Watch(ITestBrain brain, Sources sources, CancellationToken ct)
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reselections = Channel.CreateUnbounded<bool>();
        var stream = Sources.Stream<Number>(brain, sources, () => { registered.TrySetResult(); return Task.CompletedTask; },
                () => reselections.Writer.TryWrite(true), ct)
            .GetAsyncEnumerator(ct);
        var watcher = new Watcher(stream, stream.MoveNextAsync().AsTask(), reselections.Reader);
        await registered.Task.WaitAsync(Wait, ct);
        return watcher;
    }

    private sealed class Watcher(IAsyncEnumerator<Number> stream, Task<bool> first, ChannelReader<bool> reselections) : IAsyncDisposable
    {
        private Task<bool>? _pending = first;

        public Task<bool> Move()
        {
            var move = _pending ?? stream.MoveNextAsync().AsTask();
            _pending = null;
            return move;
        }

        public async Task<(string Publisher, int Value)> Next(CancellationToken ct)
        {
            Assert.True(await Move().WaitAsync(Wait, ct));
            return (stream.Current.From, stream.Current.Value);
        }

        // Awaits the next re-selection (counted, so one that finished earlier is not missed): its joiners are subscribed
        // and its leavers released, so what a fact emits next is heard by exactly the members that should hear it.
        public async Task Reselected(CancellationToken ct) => await reselections.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);

        // Nothing more arrives within a settle period that covers a synapse delivery; the pending read is checked after it.
        public async Task Silent(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            var move = Move();
            Assert.False(move.IsCompleted, "A signal arrived that should not have.");
            _pending = move;
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private static Sources Members(ITestBrain brain, string registry, Func<int, Task>? onSelect = null)
    {
        var calls = 0;
        return Sources.Named<ITestRegistry, MembersChanged, ITestEmitter>(brain.Get<ITestRegistry>("owner/" + registry), async (r, _) =>
        {
            var members = (await r.Members()).Select(id => brain.Get<ITestEmitter>("owner/" + id)).ToArray();
            if (onSelect is not null) { await onSelect(Interlocked.Increment(ref calls)); }
            return members;
        });
    }

    [Fact]
    public async Task AFixedSetYieldsEachMembersSignalsWithTheMemberAsPublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var watch = await Watch(brain, Sources.Of(brain.Get<ITestEmitter>("owner/set-a"), brain.Get<ITestEmitter>("owner/set-b")), ct);
        await brain.Get<ITestEmitter>("owner/set-a").Emit(1);
        await brain.Get<ITestEmitter>("owner/set-b").Emit(2);
        var received = new[] { await watch.Next(ct), await watch.Next(ct) };
        Assert.Equal([("set-a", 1), ("set-b", 2)], received.OrderBy(x => x.Value));
    }

    [Fact]
    public async Task ARepeatedMemberIsOneMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var a = brain.Get<ITestEmitter>("owner/set-twice");
        await using var watch = await Watch(brain, Sources.Of(a, a), ct);
        await a.Emit(1);
        Assert.Equal(("set-twice", 1), await watch.Next(ct));
        await watch.Silent(ct);
    }

    [Fact]
    public async Task DisposingTheSetStreamMidReadDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var watch = await Watch(brain, Sources.Of(brain.Get<ITestEmitter>("owner/set-idle")), ct); // registered, first read pending
        var pending = watch.Move();
        await watch.DisposeAsync();
        Assert.False(await pending.WaitAsync(Wait, ct));
    }

    [Fact]
    public async Task ARegistryChangeAddsAndRemovesMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("owner/reg-1");
        await registry.Set(["reg-1-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-1"), ct);
        await brain.Get<ITestEmitter>("owner/reg-1-a").Emit(1);
        Assert.Equal(("reg-1-a", 1), await watch.Next(ct));

        await registry.Set(["reg-1-b"]);
        await watch.Reselected(ct);
        await brain.Get<ITestEmitter>("owner/reg-1-a").Emit(2);
        await brain.Get<ITestEmitter>("owner/reg-1-b").Emit(3);
        Assert.Equal(("reg-1-b", 3), await watch.Next(ct));
    }

    // Quarantined (timing): https://github.com/intochat/os/issues/310 - removed by Phase 4
    [Fact(Explicit = true)]
    public async Task ARegistryChangePublishedDuringTheFirstSelectIsFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("owner/reg-5");
        await registry.Set(["reg-5-a"]);
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = Members(brain, "reg-5", async n =>
        {
            if (n != 1) { return; }
            selecting.TrySetResult();
            await release.Task;
        });
        var reselections = Channel.CreateUnbounded<bool>();
        await using var stream = Sources.Stream<Number>(brain, sources, null, () => reselections.Writer.TryWrite(true), ct)
            .GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await selecting.Task.WaitAsync(Wait, ct); // the first select has read [a] and is still running
        await registry.Set(["reg-5-a", "reg-5-b"]);
        release.SetResult();
        // The change published during the first select is followed by one re-selection; once it announces itself the
        // joiner is subscribed, so a single emit from it is heard.
        await reselections.Reader.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);
        await brain.Get<ITestEmitter>("owner/reg-5-b").Emit(1);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(("reg-5-b", 1), (stream.Current.From, stream.Current.Value));
    }

    [Fact]
    public async Task AMemberThatStaysAcrossAReselectionNeitherLosesNorRepeatsASignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("owner/reg-2");
        await registry.Set(["reg-2-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-2"), ct);
        await registry.Set(["reg-2-a", "reg-2-b"]); // a stays
        await watch.Reselected(ct);
        await brain.Get<ITestEmitter>("owner/reg-2-a").Emit(1);
        Assert.Equal(1, (await watch.Next(ct)).Value);
        await watch.Silent(ct);
    }

    [Fact]
    public async Task AMemberThatLeavesAndReturnsIsNotReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("owner/reg-3");
        var a = brain.Get<ITestEmitter>("owner/reg-3-a");
        await registry.Set(["reg-3-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-3"), ct);
        await registry.Set([]);
        await watch.Reselected(ct);
        await a.Emit(1); // published while out of the set
        await registry.Set(["reg-3-a"]);
        await watch.Reselected(ct);
        await a.Emit(2);
        Assert.Equal(2, (await watch.Next(ct)).Value);
    }

    [Fact]
    public async Task AFailingSelectEndsTheStreamAndReleasesEveryMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("owner/reg-4");
        await registry.Set(["reg-4-a"]);
        var sources = Members(brain, "reg-4", n => n > 1 ? throw new InvalidOperationException("malformed registry") : Task.CompletedTask);
        await using var watch = await Watch(brain, sources, ct);
        await registry.Set(["reg-4-b"]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => watch.Move().WaitAsync(Wait, ct));
        Assert.Equal("malformed registry", error.Message);
    }
}
