using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

public interface IPartCounter : INeuron { Task<int> Increment(); Task<int> Read(); Task Clear(); }

[GrainType("test.part-counter")]
public sealed class PartCounterNeuron([PersistentState("counter", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Neuron, IPartCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }
    public Task<int> Read() => Task.FromResult(state.State.Value);
    public Task Clear() => state.ClearStateAsync();
}

[GenerateSerializer, Alias("test.part-counter-state")]
public sealed class PartCounterState { [Id(0)] public int Value { get; set; } }

public interface IOutsideCounter : IGrainWithStringKey { Task Increment(); }

[GrainType("test.outside-counter")]
public sealed class OutsideCounterGrain([PersistentState("counter", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Grain, IOutsideCounter
{
    public async Task Increment() { state.State.Value++; await state.WriteStateAsync(); }
}

[Trait("host", "own")]
public sealed class BrainGrainStorageFacts
{
    private static string Scope => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    private static string Counter => $"{Scope}/apps/counter/c1";

    private static IGrainStorage Storage(ITestBrain brain, string name) => brain.Services.GetRequiredKeyedService<IGrainStorage>(name);

    [Fact(Timeout = 120_000)]
    public async Task AClearedNeuronReadsAsFreshInTheSameActivationAndAfterARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();

        await counter.Clear();

        Assert.Equal(0, await counter.Read());
        Assert.Equal(1, await counter.Increment());
        await brain.RestartAsync(ct);
        Assert.Equal(1, await brain.Get<IPartCounter>(Counter).Read());
    }

    [Fact(Timeout = 120_000)]
    public async Task ANeuronKeepsItsStateInItsPartAcrossASiloRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        for (var i = 0; i < 3; i++) { await counter.Increment(); }

        await brain.RestartAsync(ct);

        Assert.Equal(3, await brain.Get<IPartCounter>(Counter).Read());
    }

    [Fact(Timeout = 60_000)]
    public async Task NoNeuronStateReachesPlatformStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();

        var state = new GrainState<PartCounterState>();
        await Storage(brain, IntoChatOSNames.PlatformGrainStorage).ReadStateAsync("counter", counter.GetGrainId(), state);

        Assert.False(state.RecordExists);
    }

    [Fact(Timeout = 60_000)]
    public async Task AStaleETagIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();
        await counter.Increment();
        var direct = Storage(brain, IntoChatOSNames.LegacyClusterGrainStorage);

        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");

        await Assert.ThrowsAsync<InconsistentStateException>(() => direct.WriteStateAsync("counter", counter.GetGrainId(), stale));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedWriteKeepsTheSignalsOfItsSavePending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();
        await counter.Increment();
        var direct = Storage(brain, IntoChatOSNames.LegacyClusterGrainStorage);
        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");
        Signal[] signals = [new Signal()];

        using (SignalJournal.Attach(counter.GetGrainId(), signals))
        {
            await Assert.ThrowsAsync<InconsistentStateException>(() => direct.WriteStateAsync("counter", counter.GetGrainId(), stale));

            Assert.Single(SignalJournal.TakePending(counter.GetGrainId()));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AKeyOutsideEveryBrainIsRefusedOnAMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => brain.Grains.GetGrain<IOutsideCounter>("@platform/x").Increment());

        Assert.IsType<ArgumentException>(error as ArgumentException ?? error.InnerException);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheKernelRegistersANeuronWrittenToItsPartLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);

        await brain.Get<IPartCounter>(Counter).Increment();

        Assert.Contains(await Listed(brain, ct), instance => instance.Key == Counter);
    }

    private static async Task<List<Instance>> Listed(ITestBrain brain, CancellationToken ct)
    {
        List<Instance> listed = [];
        await foreach (var instance in new global::IntoChat.OS.Kernel.Composition.Kernel(brain.Grains).InstancesIn(Scope, null, ct)) { listed.Add(instance); }
        return listed;
    }
}
