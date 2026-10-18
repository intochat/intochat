using DigitalBrain;
using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Integration;

public sealed class SourceSetFacts(ClientHost host)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Opens the set and returns once every member's subscription is registered, with the first read pending.
    private static async Task<Watcher> Watch(ITestBrain brain, Sources sources, CancellationToken ct)
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = SourceSet.Open<Number>(brain, sources, () => { registered.TrySetResult(); return Task.CompletedTask; }, coalesce: false, ct)
            .GetAsyncEnumerator(ct);
        var watcher = new Watcher(stream, stream.MoveNextAsync().AsTask());
        await registered.Task.WaitAsync(Wait, ct);
        return watcher;
    }

    private sealed class Watcher(IAsyncEnumerator<Number> stream, Task<bool> first) : IAsyncDisposable
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
            return (stream.Current.Publisher.Name, stream.Current.Value);
        }

        public async Task Silent(CancellationToken ct)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
            silence.CancelAfter(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Move().WaitAsync(silence.Token));
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    // A re-selection after a registry change has no observable gate; give it time to register joiners and drop leavers.
    private static Task Reselected(CancellationToken ct) => Task.Delay(300, ct);

    private static Sources Members(ITestBrain brain, string registry, Func<int, Task>? onSelect = null)
    {
        var calls = 0;
        return Sources.Named<ITestRegistry, MembersChanged>(brain.Get<ITestRegistry>(registry), async (r, _) =>
        {
            var members = (await r.Members()).Select(id => (INeuron)brain.Get<ITestEmitter>(id)).ToArray();
            if (onSelect is not null) { await onSelect(Interlocked.Increment(ref calls)); }
            return members;
        });
    }

    [Fact]
    public async Task AFixedSetYieldsEachMembersSignalsWithTheMemberAsPublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var watch = await Watch(brain, Sources.Of(brain.Get<ITestEmitter>("set-a"), brain.Get<ITestEmitter>("set-b")), ct);
        await brain.Get<ITestEmitter>("set-a").Emit(1);
        await brain.Get<ITestEmitter>("set-b").Emit(2);
        var received = new[] { await watch.Next(ct), await watch.Next(ct) };
        Assert.Equal([("test-emitter/set-a", 1), ("test-emitter/set-b", 2)], received.OrderBy(x => x.Value));
    }

    [Fact]
    public async Task ARepeatedMemberIsOneMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var a = brain.Get<ITestEmitter>("set-twice");
        await using var watch = await Watch(brain, Sources.Of(a, a), ct);
        await a.Emit(1);
        Assert.Equal(("test-emitter/set-twice", 1), await watch.Next(ct));
        await watch.Silent(ct);
    }

    [Fact]
    public async Task DisposingTheSetStreamMidReadDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        IDigitalBrain program = brain;
        var stream = program.On<Number>(Sources.Of(brain.Get<ITestEmitter>("set-idle")), ct).GetAsyncEnumerator(ct);
        var pending = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await stream.DisposeAsync();
        Assert.False(await pending.WaitAsync(Wait, ct));
    }

    [Fact]
    public async Task ARegistryChangeAddsAndRemovesMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("reg-1");
        await registry.Set(["reg-1-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-1"), ct);
        await brain.Get<ITestEmitter>("reg-1-a").Emit(1);
        Assert.Equal(("test-emitter/reg-1-a", 1), await watch.Next(ct));

        await registry.Set(["reg-1-b"]);
        await Reselected(ct);
        await brain.Get<ITestEmitter>("reg-1-a").Emit(2);
        await brain.Get<ITestEmitter>("reg-1-b").Emit(3);
        Assert.Equal(("test-emitter/reg-1-b", 3), await watch.Next(ct));
    }

    [Fact]
    public async Task ARegistryChangePublishedDuringTheFirstSelectIsFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        IDigitalBrain program = brain;
        var registry = brain.Get<ITestRegistry>("reg-5");
        await registry.Set(["reg-5-a"]);
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = Members(brain, "reg-5", async n =>
        {
            if (n != 1) { return; }
            selecting.TrySetResult();
            await release.Task;
        });
        await using var stream = program.On<Number>(sources, ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await selecting.Task.WaitAsync(Wait, ct); // the first select has read [a] and is still running
        await registry.Set(["reg-5-a", "reg-5-b"]);
        release.SetResult();
        await Reselected(ct);
        await brain.Get<ITestEmitter>("reg-5-b").Emit(1);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(("test-emitter/reg-5-b", 1), (stream.Current.Publisher.Name, stream.Current.Value));
    }

    [Fact]
    public async Task AMemberThatStaysAcrossAReselectionNeitherLosesNorRepeatsASignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("reg-2");
        await registry.Set(["reg-2-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-2"), ct);
        await registry.Set(["reg-2-a", "reg-2-b"]); // a stays
        await brain.Get<ITestEmitter>("reg-2-a").Emit(1);
        Assert.Equal(1, (await watch.Next(ct)).Value);
        await watch.Silent(ct);
    }

    [Fact]
    public async Task AMemberThatLeavesAndReturnsIsNotReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("reg-3");
        var a = brain.Get<ITestEmitter>("reg-3-a");
        await registry.Set(["reg-3-a"]);
        await using var watch = await Watch(brain, Members(brain, "reg-3"), ct);
        await registry.Set([]);
        await Reselected(ct);
        await a.Emit(1); // published while out of the set
        await registry.Set(["reg-3-a"]);
        await Reselected(ct);
        await a.Emit(2);
        Assert.Equal(2, (await watch.Next(ct)).Value);
    }

    [Fact]
    public async Task AFailingSelectEndsTheStreamAndReleasesEveryMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var registry = brain.Get<ITestRegistry>("reg-4");
        await registry.Set(["reg-4-a"]);
        var sources = Members(brain, "reg-4", n => n > 1 ? throw new InvalidOperationException("malformed registry") : Task.CompletedTask);
        await using var watch = await Watch(brain, sources, ct);
        await registry.Set(["reg-4-b"]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => watch.Move().WaitAsync(Wait, ct));
        Assert.Equal("malformed registry", error.Message);
    }
}
