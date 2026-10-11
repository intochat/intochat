using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public interface IPartCounter : INeuron { Task<int> Increment(); Task<int> Read(); Task Clear(); }

[GrainType("test.part-counter")]
public sealed class PartCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Neuron, IPartCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }
    public Task<int> Read() => Task.FromResult(state.State.Value);
    public Task Clear() => state.ClearStateAsync();
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
    private static string Scope => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    private static string Counter => $"{Scope}/apps/counter/c1";

    private static IGrainStorage Storage(ITestBrain brain, string name) => brain.Services.GetRequiredKeyedService<IGrainStorage>(name);

    [Fact(Timeout = 120_000)]
    public async Task AClearedNeuronReadsAsFreshInTheSameActivationAndAfterARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
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
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        for (var i = 0; i < 3; i++) { await counter.Increment(); }

        await brain.RestartAsync(ct);

        Assert.Equal(3, await brain.Get<IPartCounter>(Counter).Read());
        Assert.NotNull(await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf($"{Scope}/apps/counter")).Get());
    }

    [Fact(Timeout = 60_000)]
    public async Task NoNeuronStateReachesPlatformStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
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
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();
        await counter.Increment();
        var storage = brain.Services.GetRequiredService<PartLogs>();
        var direct = new PartGrainStorage(storage, brain.Services.GetRequiredService<Orleans.Serialization.Serializer>());

        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");

        await Assert.ThrowsAsync<InconsistentStateException>(() => direct.WriteStateAsync("counter", counter.GetGrainId(), stale));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedWriteKeepsTheSignalsOfItsSavePending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var counter = brain.Get<IPartCounter>(Counter);
        await counter.Increment();
        await counter.Increment();
        var direct = new PartGrainStorage(brain.Services.GetRequiredService<PartLogs>(), brain.Services.GetRequiredService<Orleans.Serialization.Serializer>());
        var stale = new GrainState<PartCounterState>(new PartCounterState { Value = 9 }, "0");
        Signal[] signals = [new Signal()];

        using (SignalJournal.Attach(counter.GetGrainId(), signals))
        {
            await Assert.ThrowsAsync<InconsistentStateException>(() => direct.WriteStateAsync("counter", counter.GetGrainId(), stale));

            Assert.Single(SignalJournal.TakePending(counter.GetGrainId()));
        }
    }

    // Segments already in the mesh were written by earlier builds: a segment in today's LogEntry shape must read
    // back whole. A member added to LogEntry (even one marked not serialized) changed its codec, and the live
    // owner part replayed null entries.
    private const string WrittenSegment = "20001D21000321001D4103674103730105410701020321E0E0E0C101E0";

    [Fact(Timeout = 60_000)]
    public async Task ASegmentWrittenByTheEarlierBuildReadsBackWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Orleans.Serialization.Serializer>();
        var segment = new LogSegment
        {
            FirstSeq = 7,
            Entries = [new LogEntry { Seq = 7, GrainId = "g", StateName = "s", Change = RecordChange.Write, Record = [1, 2, 3], Signals = [] }],
        };

        Assert.Equal(WrittenSegment, Convert.ToHexString(serializer.SerializeToArray(segment)));
        var read = serializer.Deserialize<LogSegment>(Convert.FromHexString(WrittenSegment));
        var entry = Assert.Single(read!.Entries);
        Assert.Equal((7L, "g", "s", 3), (entry.Seq, entry.GrainId, entry.StateName, entry.Record.Length));
    }

    [Fact(Timeout = 60_000)]
    public async Task AKeyOutsideEveryBrainIsRefusedOnAMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => brain.Grains.GetGrain<IOutsideCounter>("@platform/x").Increment());

        Assert.IsType<ArgumentException>(error as ArgumentException ?? error.InnerException);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheKernelRegistersANeuronWrittenToItsPartLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);

        await brain.Get<IPartCounter>(Counter).Increment();

        Assert.Contains(await Listed(brain, ct), instance => instance.Key == Counter);
    }

    private static async Task<List<Instance>> Listed(ITestBrain brain, CancellationToken ct)
    {
        List<Instance> listed = [];
        await foreach (var instance in new Framework.Kernel(brain.Grains).InstancesIn(Scope, null, ct)) { listed.Add(instance); }
        return listed;
    }
}
