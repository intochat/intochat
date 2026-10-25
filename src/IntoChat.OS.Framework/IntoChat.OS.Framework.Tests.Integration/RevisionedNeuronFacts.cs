using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration;

[GenerateSerializer, Alias("test.tally-state")]
public sealed record TallyState : IRevisioned<TallyState>
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public string[] Operations { get; init; } = [];
    [Id(2)] public int Value { get; init; }
    public TallyState Advanced(string operationId) => this with { Revision = Revision + 1, Operations = [.. Operations.TakeLast(63), operationId] };
}

[GenerateSerializer, Alias("test.tally-changed")]
public sealed record TallyChanged([property: Id(0)] long Revision) : Signal;

[Alias("test.tally")]
public interface ITally : INeuron
{
    Task Add(string operationId, long expectedRevision, int by);
    Task<TallyState> Read();
}

[GrainType("test-tally")]
public sealed class TallyNeuron([PersistentState("tally", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TallyState> store) : RevisionedNeuron<TallyState>(store), ITally
{
    public Task Add(string operationId, long expectedRevision, int by) =>
        Commit(operationId, expectedRevision, s => s with { Value = s.Value + by }, s => new TallyChanged(s.Revision));

    public Task<TallyState> Read() => Task.FromResult(Snapshot);
}

public sealed class RevisionedNeuronFacts(FrameworkHost host)
{
    [Fact]
    public async Task AStaleRevisionIsRefusedWithTheCurrentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var tally = brain.Get<ITally>("stale");
        await tally.Add("a", 0, 1);
        var conflict = await Assert.ThrowsAsync<RevisionConflictException>(() => tally.Add("b", 0, 1));
        Assert.Equal(1, conflict.CurrentRevision);
        Assert.Equal(1, (await tally.Read()).Value);
    }

    [Fact]
    public async Task AReplayedOperationChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var tally = brain.Get<ITally>("replay");
        await tally.Add("a", 0, 1);
        await tally.Add("a", 0, 1);
        var state = await tally.Read();
        Assert.Equal(1, state.Value);
        Assert.Equal(1, state.Revision);
    }

    [Fact]
    public async Task AReplayOutlivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var tally = brain.Get<ITally>("reactivated");
        await tally.Add("a", 0, 1);
        await brain.DeactivateAsync(tally, ct);
        await tally.Add("a", 0, 1);
        var state = await tally.Read();
        Assert.Equal(1, state.Value);
        Assert.Equal(1, state.Revision);
    }
}
