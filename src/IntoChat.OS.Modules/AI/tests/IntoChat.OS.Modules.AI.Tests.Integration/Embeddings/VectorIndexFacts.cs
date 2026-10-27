using IntoChat.OS.Modules.AI.Embeddings;
using IntoChat.OS.Modules.AI.Embeddings.Signals;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Embeddings;

public sealed class VectorIndexFacts(AIHost host)
{
    [Fact]
    public async Task ANearestQueryRanksTheReplacedEntriesByCosine()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await using var changed = await brain.Observe<VectorIndexChanged>(index, ct);

        var snapshot = await index.Replace([Entry("north", 0, 1), Entry("east", 1, 0), Entry("northeast", 1, 1)]);
        var matches = await index.Nearest([0.1f, 1], 2);

        Assert.Equal(new VectorIndexSnapshot(3, 2, 1), snapshot);
        Assert.Equal(["north", "northeast"], matches.Select(match => match.Id));
        Assert.Equal(1 / Math.Sqrt(1.01), matches[0].Score, 6);
        Assert.Equal(1.1 / Math.Sqrt(2.02), matches[1].Score, 6);
        Assert.Equal((Notes(person), 1L, 3), Fields(await changed.NextAsync(ct: ct)));
    }

    [Fact]
    public async Task AReplaceDropsTheEntriesItIsNotGivenAndTakesTheirDimensions()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Replace([Entry("north", 0, 1), Entry("east", 1, 0)]);

        var snapshot = await index.Replace([Entry("up", 0, 0, 1)]);

        Assert.Equal(new VectorIndexSnapshot(1, 3, 2), snapshot);
        Assert.Equal(["up"], (await index.Nearest([0, 1, 0], 5)).Select(match => match.Id));
    }

    [Fact]
    public async Task AnUpsertReplacesByIdAndARemoveDropsTheGivenIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Upsert([Entry("a", 1, 0), Entry("b", 0, 1)]);

        var upserted = await index.Upsert([Entry("b", 1, 0, ("note", "moved")), Entry("c", 1, 1)]);
        var matches = await index.Nearest([1, 0], 3);
        var removed = await index.Remove(["a", "missing"]);

        Assert.Equal(new VectorIndexSnapshot(3, 2, 2), upserted);
        Assert.Equal(["a", "b", "c"], matches.Select(match => match.Id));
        Assert.Equal("moved", matches[1].Payload["note"]);
        Assert.Equal(new VectorIndexSnapshot(2, 2, 3), removed);
        Assert.Equal(["b", "c"], (await index.Nearest([1, 0], 3)).Select(match => match.Id));
    }

    [Fact]
    public async Task ARedeliveredWriteLeavesTheRevisionWhereItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        VectorEntry[] entries = [Entry("a", 1, 0, ("app", "files")), Entry("b", 0, 1)];
        await index.Replace(entries);

        Assert.Equal(new VectorIndexSnapshot(2, 2, 1), await index.Replace(entries));
        Assert.Equal(new VectorIndexSnapshot(2, 2, 1), await index.Upsert([entries[1]]));
        Assert.Equal(new VectorIndexSnapshot(2, 2, 1), await index.Remove(["missing"]));
    }

    [Fact]
    public async Task AMismatchedDimensionIsRefusedAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Replace([Entry("a", 1, 0)]);

        await Assert.ThrowsAsync<ArgumentException>(() => index.Upsert([Entry("b", 1, 0, 0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => index.Replace([Entry("x", 1, 0), Entry("y", 1, 0, 0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => index.Nearest([1, 0, 0], 1));

        Assert.Equal(new VectorIndexSnapshot(1, 2, 1), await index.Read());
    }

    [Fact]
    public async Task AnEmptiedIndexTakesItsDimensionsFromTheNextWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Upsert([Entry("a", 1, 0)]);

        Assert.Equal(new VectorIndexSnapshot(0, 0, 2), await index.Remove(["a"]));
        Assert.Empty(await index.Nearest([1, 0], 1));
        Assert.Equal(new VectorIndexSnapshot(1, 3, 3), await index.Upsert([Entry("b", 0, 0, 1)]));
    }

    [Fact]
    public async Task MustMatchKeepsOnlyTheEntriesWhosePayloadHoldsEveryPair()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Replace([
            Entry("files", 1, 0, ("kind", "spec"), ("app", "files")),
            Entry("files/open.cs", 1, 0, ("kind", "synapse"), ("app", "files")),
            Entry("memory/recall.cs", 0.9f, 0.1f, ("kind", "synapse"), ("app", "memory"))]);

        Assert.Equal(["files/open.cs", "memory/recall.cs"],
            (await index.Nearest([1, 0], 5, new() { ["kind"] = "synapse" })).Select(match => match.Id));
        Assert.Equal(["memory/recall.cs"],
            (await index.Nearest([1, 0], 5, new() { ["kind"] = "synapse", ["app"] = "memory" })).Select(match => match.Id));
        Assert.Empty(await index.Nearest([1, 0], 5, new() { ["kind"] = "shared" }));
    }

    [Fact]
    public async Task TheIndexSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var index = brain.Get<IVectorIndex>(Notes(person));
        await index.Replace([Entry("north", 0, 1, ("title", "North")), Entry("east", 1, 0, ("title", "East"))]);

        await brain.DeactivateAsync(index, ct);
        var match = Assert.Single(await index.Nearest([0, 1], 1));

        Assert.Equal(new VectorIndexSnapshot(2, 2, 1), await index.Read());
        Assert.Equal(("north", "North"), (match.Id, match.Payload["title"]));
        Assert.Equal(new VectorIndexSnapshot(3, 2, 2), await index.Upsert([Entry("west", -1, 0)]));
    }

    [Fact]
    public async Task AnIndexKeyedUnderAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var elsewhere = brain.Get<IVectorIndex>(IVectorIndex.Key("elsewhere", "notes"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(elsewhere.Read);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => elsewhere.Replace([Entry("a", 1, 0)]));
    }

    [Fact]
    public async Task ASearchNeverCrossesBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var first = await host.PersonAsync(ct: ct);
        var second = await host.PersonAsync(ct: ct);
        Assert.NotEqual(first.BrainId, second.BrainId);
        static VectorEntry[] Life(Person person, string id) =>
            [new(id, [1, 0], new() { ["tier"] = "fact", ["brain"] = person.BrainId })];
        using (first.AsCaller())
        { await brain.Get<IVectorIndex>(IVectorIndex.Key(first.BrainId, "life")).Upsert(Life(first, "fact:first")); }
        using (second.AsCaller())
        { await brain.Get<IVectorIndex>(IVectorIndex.Key(second.BrainId, "life")).Upsert(Life(second, "fact:second")); }

        using (first.AsCaller())
        {
            var own = brain.Get<IVectorIndex>(IVectorIndex.Key(first.BrainId, "life"));
            Assert.Equal(["fact:first"], (await own.Nearest([1, 0], 10, new() { ["brain"] = first.BrainId })).Select(match => match.Id));
            Assert.Empty(await own.Nearest([1, 0], 10, new() { ["brain"] = second.BrainId }));
            var other = brain.Get<IVectorIndex>(IVectorIndex.Key(second.BrainId, "life"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.Nearest([1, 0], 10, new() { ["brain"] = second.BrainId }));
        }
    }

    private static string Notes(Person person) => IVectorIndex.Key(person.BrainId, "notes");

    private static VectorEntry Entry(string id, params float[] vector) => new(id, vector, []);

    private static VectorEntry Entry(string id, float x, float y, params (string Key, string Value)[] payload)
        => new(id, [x, y], payload.ToDictionary(field => field.Key, field => field.Value));

    private static (string, long, int) Fields(VectorIndexChanged changed) => (changed.Index, changed.Revision, changed.Count);
}
