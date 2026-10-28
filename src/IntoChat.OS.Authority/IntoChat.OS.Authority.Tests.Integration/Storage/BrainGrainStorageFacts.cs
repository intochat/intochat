using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

file sealed record HeldSignal : DigitalBrain.Signal;

// A raw grain on the cluster store, as the Authority grains that stay grains are (06-08: the Framework neuron base is gone).
public interface IPartCounter : IGrainWithStringKey { Task<int> Increment(); Task<int> Read(); Task Clear(); }

[GrainType("test.part-counter")]
public sealed class PartCounterNeuron([PersistentState("counter", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Grain, IPartCounter
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
        var counter = PartCounter(brain);
        await counter.Increment();

        await counter.Clear();

        Assert.Equal(0, await counter.Read());
        Assert.Equal(1, await counter.Increment());
        await brain.RestartAsync(ct);
        Assert.Equal(1, await PartCounter(brain).Read());
    }

    [Fact(Timeout = 120_000)]
    public async Task ANeuronKeepsItsStateInItsPartAcrossASiloRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = PartCounter(brain);
        for (var i = 0; i < 3; i++) { await counter.Increment(); }

        await brain.RestartAsync(ct);

        Assert.Equal(3, await PartCounter(brain).Read());
    }

    [Fact(Timeout = 60_000)]
    public async Task NoNeuronStateReachesPlatformStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = PartCounter(brain);
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
        var counter = PartCounter(brain);
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
        var counter = PartCounter(brain);
        await counter.Increment();
        await counter.Increment();
        var direct = Storage(brain, IntoChatOSNames.LegacyClusterGrainStorage);
        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");
        DigitalBrain.Signal[] signals = [new HeldSignal()];

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

    // The probe is a raw grain, reached as the Authority's own grains are (06-08).
    private static IPartCounter PartCounter(ITestBrain brain) => brain.Grains.GetGrain<IPartCounter>(Counter);
}
