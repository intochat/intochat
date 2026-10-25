using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration;

[Alias("test.counter")]
public interface ICounter : INeuron
{
    Task<int> Read();
    Task Set(int value);
    Task SetWithoutPublishing(int value);
}
[GenerateSerializer]
public sealed class CounterState { [Id(0)] public int Value { get; set; } }
[GrainType("test-counter")]
public sealed class CounterNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CounterState> state) : Neuron, ICounter
{
    public Task<int> Read() => Task.FromResult(state.State.Value);
    public async Task Set(int value)
    {
        await SetWithoutPublishing(value);
        await PublishAsync(new Number(value));
    }
    public async Task SetWithoutPublishing(int value)
    {
        state.State = new CounterState { Value = value };
        await state.WriteStateAsync();
    }
}

[GenerateSerializer]
public sealed class ProbeState { [Id(0)] public int Value { get; set; } }

[Alias("test.probe")]
public interface IProbe : INeuron
{
    Task<bool> Saved();
    Task Set(int value);
}

[GrainType("test-probe")]
public sealed class ProbeNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ProbeState> state) : Neuron<ProbeState>(state), IProbe
{
    public int Value => Snapshot.Value;
    public Task<bool> Saved() => Task.FromResult(Exists);
    public Task Set(int value) => Save(new ProbeState { Value = value }, new Number(value));
}

internal sealed class FailingProbeState(ProbeState initial, bool exists = true) : IPersistentState<ProbeState>
{
    public ProbeState State { get; set; } = initial;
    public string Etag => string.Empty;
    public bool RecordExists => exists;
    public bool FailNextWrite { get; set; }
    public Task ClearStateAsync() => Task.CompletedTask;
    public Task ReadStateAsync() => Task.CompletedTask;
    public Task WriteStateAsync() => FailNextWrite
        ? Task.FromException(new InvalidOperationException("Storage is unavailable."))
        : Task.CompletedTask;
}

public sealed class PersistenceFacts(FrameworkHost host)
{
    [Fact]
    public async Task FailedWriteRollsStateBack()
    {
        var storage = new FailingProbeState(new ProbeState { Value = 7 });
        var neuron = new ProbeNeuron(storage);
        storage.FailNextWrite = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => neuron.Set(9));
        Assert.Equal(7, neuron.Value);
        Assert.Equal(7, storage.State.Value);
    }

    [Fact]
    public async Task AFailedFirstWriteLeavesTheRecordAbsent()
    {
        var store = new FailingProbeState(new ProbeState(), exists: false) { FailNextWrite = true };
        var probe = new ProbeNeuron(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.Set(1));
        Assert.False(await probe.Saved());
    }

    [Fact]
    public async Task ExistsIsFalseUntilTheFirstSaveAndSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var probe = brain.Get<IProbe>("fresh");
        Assert.False(await probe.Saved());
        await probe.Set(2);
        Assert.True(await probe.Saved());
        await brain.DeactivateAsync(probe, ct);
        Assert.True(await probe.Saved());
    }

    [Fact]
    public async Task DeactivationKeepsStateAndDoesNotReplaySignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var counter = brain.Get<ICounter>("saved");
        await counter.SetWithoutPublishing(23);
        await brain.DeactivateAsync(counter, ct);
        Assert.Equal(23, await counter.Read());
        await using var probe = await brain.Observe<Number>(counter, ct);
        await counter.Set(24);
        Assert.Equal(24, (await probe.NextAsync(ct: ct)).Value);
        Assert.DoesNotContain(probe.Snapshot, fact => fact.Value == 23);
    }
}
