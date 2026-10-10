using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public interface IPartCounter : INeuron { Task<int> Increment(); Task<int> Read(); }

[GrainType("test.part-counter")]
public sealed class PartCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Neuron, IPartCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }
    public Task<int> Read() => Task.FromResult(state.State.Value);
}

[GenerateSerializer, Alias("test.part-counter-state")]
public sealed class PartCounterState { [Id(0)] public int Value { get; set; } }

public interface IOutsideCounter : IGrainWithStringKey { Task Increment(); }

[GrainType("test.outside-counter")]
public sealed class OutsideCounterGrain([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Grain, IOutsideCounter
{
    public async Task Increment() { state.State.Value++; await state.WriteStateAsync(); }
}

public sealed class PartGrainStorageFacts
{
    private static string Scope => BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;

    private static string Counter => $"{Scope}/apps/counter/c1";

    private static IGrainStorage Storage(ModuleBrain brain, string name) => brain.SiloServices.GetRequiredKeyedService<IGrainStorage>(name);

    [Fact(Timeout = 120_000)]
    public async Task ANeuronKeepsItsStateInItsPartAcrossASiloRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        for (var i = 0; i < 3; i++) { await counter.Increment(); }

        await brain.RestartSiloAsync(ct);

        Assert.Equal(3, await brain.Get<IPartCounter>(Counter).Read());
        Assert.NotNull(await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf($"{Scope}/apps/counter")).Get());
    }

    [Fact(Timeout = 60_000)]
    public async Task NoNeuronStateReachesPlatformStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();

        var state = new GrainState<PartCounterState>();
        await Storage(brain, DigitalBrainNames.PlatformGrainStorage).ReadStateAsync("counter", counter.GetGrainId(), state);

        Assert.False(state.RecordExists);
    }

    [Fact(Timeout = 60_000)]
    public async Task AStaleETagIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();
        await counter.Increment();
        var storage = brain.SiloServices.GetRequiredService<PartLogs>();
        var direct = new PartGrainStorage(storage, brain.SiloServices.GetRequiredService<Orleans.Serialization.Serializer>());

        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");

        await Assert.ThrowsAsync<InconsistentStateException>(() => direct.WriteStateAsync("counter", counter.GetGrainId(), stale));
    }

    [Fact(Timeout = 60_000)]
    public async Task AKeyOutsideEveryBrainIsRefusedOnAMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => brain.Grains.GetGrain<IOutsideCounter>("@platform/x").Increment());

        Assert.IsType<ArgumentException>(error as ArgumentException ?? error.InnerException);
    }
}
