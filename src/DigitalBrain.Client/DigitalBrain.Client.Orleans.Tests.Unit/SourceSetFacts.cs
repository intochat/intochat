using DigitalBrain;
using DigitalBrain.Testing.Module;
using Xunit;

namespace DigitalBrain.Client.Orleans.Tests.Unit;

public sealed class SourceSetFacts
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AFixedSetYieldsEachMembersSignalsWithTheMemberAsPublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var a = brain.Get<ITestEmitter>("set-a");
        var b = brain.Get<ITestEmitter>("set-b");
        await using var stream = program.On<Number>(Sources.Of(a, b), ct).GetAsyncEnumerator(ct);
        var first = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct); // both members register before anything is published
        await a.Emit(1);
        await b.Emit(2);
        Assert.True(await first.WaitAsync(Wait, ct));
        var one = (stream.Current.Publisher.Name, stream.Current.Value);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Wait, ct));
        var two = (stream.Current.Publisher.Name, stream.Current.Value);
        Assert.Equal([("test-emitter/set-a", 1), ("test-emitter/set-b", 2)], new[] { one, two }.OrderBy(x => x.Value));
    }

    [Fact]
    public async Task DisposingTheSetStreamMidReadDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var stream = program.On<Number>(Sources.Of(brain.Get<ITestEmitter>("set-idle")), ct).GetAsyncEnumerator(ct);
        var pending = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await stream.DisposeAsync();
        Assert.False(await pending.WaitAsync(Wait, ct));
    }

    private static Sources Members(ModuleBrain brain, string registry)
        => Sources.Named<ITestRegistry, MembersChanged>(brain.Get<ITestRegistry>(registry),
            async (r, ct) => (await r.Members()).Select(id => (INeuron)brain.Get<ITestEmitter>(id)).ToArray());

    [Fact]
    public async Task ARegistryChangeAddsAndRemovesMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var registry = brain.Get<ITestRegistry>("reg-1");
        await registry.Set(["reg-1-a"]);
        await using var stream = program.On<Number>(Members(brain, "reg-1"), ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await brain.Get<ITestEmitter>("reg-1-a").Emit(1);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(("test-emitter/reg-1-a", 1), (stream.Current.Publisher.Name, stream.Current.Value));

        await registry.Set(["reg-1-b"]);
        await Task.Delay(300, ct); // the re-selection registers b and drops a
        next = stream.MoveNextAsync().AsTask();
        await brain.Get<ITestEmitter>("reg-1-a").Emit(2);
        await brain.Get<ITestEmitter>("reg-1-b").Emit(3);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(("test-emitter/reg-1-b", 3), (stream.Current.Publisher.Name, stream.Current.Value));
    }

    [Fact]
    public async Task AMemberThatStaysAcrossAReselectionNeitherLosesNorRepeatsASignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var registry = brain.Get<ITestRegistry>("reg-2");
        await registry.Set(["reg-2-a"]);
        await using var stream = program.On<Number>(Members(brain, "reg-2"), ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await registry.Set(["reg-2-a", "reg-2-b"]); // a stays
        await brain.Get<ITestEmitter>("reg-2-a").Emit(1);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(1, stream.Current.Value);
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.MoveNextAsync().AsTask().WaitAsync(silence.Token));
    }

    [Fact]
    public async Task AMemberThatLeavesAndReturnsIsNotReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var registry = brain.Get<ITestRegistry>("reg-3");
        var a = brain.Get<ITestEmitter>("reg-3-a");
        await registry.Set(["reg-3-a"]);
        await using var stream = program.On<Number>(Members(brain, "reg-3"), ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await registry.Set([]);
        await Task.Delay(300, ct);
        await a.Emit(1); // published while out of the set
        await registry.Set(["reg-3-a"]);
        await Task.Delay(300, ct);
        await a.Emit(2);
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal(2, stream.Current.Value);
    }

    [Fact]
    public async Task AFailingSelectEndsTheStreamAndReleasesEveryMember()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        IDigitalBrain program = brain;
        var registry = brain.Get<ITestRegistry>("reg-4");
        await registry.Set(["reg-4-a"]);
        var calls = 0;
        var sources = Sources.Named<ITestRegistry, MembersChanged>(registry, async (r, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) { throw new InvalidOperationException("malformed registry"); }
            return (await r.Members()).Select(id => (INeuron)brain.Get<ITestEmitter>(id)).ToArray();
        });
        await using var stream = program.On<Number>(sources, ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await registry.Set(["reg-4-b"]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => next.WaitAsync(Wait, ct));
        Assert.Equal("malformed registry", error.Message);
    }
}
