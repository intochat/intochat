using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;

namespace IntoChat.OS.Tests.Integration.Modules.Postgres;

public sealed class TableNeuronFacts
{
    private static readonly TableDefinition BookmarksSchema = new(
        [new("id", "text"), new("title", "text")],
        ["id"]);

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARowUpsertedByItsOwnerIsReadBack(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, f => f.Postgres(), TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");

        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            var accepted = await table.Define(BookmarksSchema);
            var changed = await table.Upsert([new("id", "\"1\"")], [new("title", "\"Antigravity\"")]);
            var row = await table.Read([new("id", "\"1\"")]);

            Assert.True(changed);
            Assert.NotNull(row);
            Assert.Equal("\"1\"", row.Single(v => v.Column == "id").Json);
            Assert.Equal("\"Antigravity\"", row.Single(v => v.Column == "title").Json);

            var store = os.Fake<FakeTableStore>();
            Assert.Contains(accepted.Table, store.Tables);
            Assert.Single(store.Rows(accepted.Table));
        }
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnotherAppOfTheSameBrainIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, f => f.Postgres(), TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");

        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            await table.Define(BookmarksSchema);
            await table.Upsert([new("id", "\"1\"")], [new("title", "\"Secret\"")]);
        }

        using (ada.EnterAsApp("other-app"))
        {
            var table = ada.Get<ITable>(id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Read([new("id", "\"1\"")]));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Upsert([new("id", "\"2\"")], [new("title", "\"Nope\"")]));
        }
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APersonCallerWithNoAppIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, f => f.Postgres(), TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");

        var table = ada.Get<ITable>(id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Define(BookmarksSchema));
    }
}
