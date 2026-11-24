using DigitalBrain;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// IMemory.RecallPack is [ReadOnlyCall, Interleaves]: it may run beside a write turn of the same activation, and a caller offered
// read-only access may make it. It used to copy unsynced memory into the knowledge store and save `Synced` on the way, a write
// from inside an interleaving read that races the writer's version. The copy now happens when the neuron activates.
public sealed class MemoryRecallIsReadOnlyFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly BrainId Brain = new("recall-readonly");

    [Fact]
    public async Task ARecallOfUnsyncedMemoryWritesNoState()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("ada", "ada", Brain));
        var memory = fixture.Runtime.Get<IMemory>(new NeuronId(Brain, "ai.memory", "recall-writes-nothing"));
        // A preference is stored without the one-time copy of earlier memory, so the memory is not yet synced.
        await memory.RememberPreference("owner", "deploy", "Rollouts need a confirmation");
        var before = (await memory.Read()).Revision;

        await memory.RecallPack("rollout confirmation");
        await memory.Recall("rollout confirmation");

        Assert.Equal(before, (await memory.Read()).Revision);
    }

    [Fact]
    public async Task UnsyncedMemoryReachesTheStoreWhenTheNeuronActivates()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("ada", "ada", Brain));
        var id = new NeuronId(Brain, "ai.memory", "synced-on-activation");
        var memory = fixture.Runtime.Get<IMemory>(id);
        var store = fixture.Runtime.Get<IKnowledgeStore>(new NeuronId(Brain, "ai.knowledge-store", KnowledgeGraphSource.StoreKey));
        await memory.RememberPreference("owner", "deploy", "Rollouts need a confirmation");
        var before = (await memory.Read()).Revision;

        await fixture.Runtime.Deactivate(id);
        var after = await memory.Read();

        Assert.Equal(before + 1, after.Revision);
        var note = Assert.Single(after.Notes);
        Assert.NotNull(await store.GetAsync(note.Id, TestContext.Current.CancellationToken));
        Assert.Contains((await memory.RecallPack("rollout confirmation")).OwnerRules, rule => rule.Id == note.Id);
    }
}
