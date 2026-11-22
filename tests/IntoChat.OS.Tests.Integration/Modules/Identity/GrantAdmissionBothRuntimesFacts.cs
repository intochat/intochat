using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Modules.Identity;

public sealed class GrantAdmissionBothRuntimesFacts
{
    private static readonly TableDefinition Schema = new(
        [new("id", "text"), new("note", "text")],
        ["id"]);

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnAppWithoutAGrantIsDeniedIdenticallyOnTestOSAndSiloAndAdmittedOnceGranted(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(
            kind,
            f => f.Postgres().Grants(),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var tableId = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        var target = $"{tableId.Type}/{tableId.Brain.Value}/{tableId.Key}";

        // Without a grant, an app calling a neuron outside its own app key is denied by GrantCallFilterStage on both runtimes.
        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(tableId);
            var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Define(Schema));
            Assert.Contains("requires a valid grant", denied.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Adding a Run grant admits the exact same call on both TestOS and Silo.
        var tableGrant = Grant.Tuple(
            owner: "brain:ada",
            grantee: "app:ada/apps/bookmarks",
            target: target,
            relation: GrantRelations.Run);
        var indexGrant = Grant.Tuple(
            owner: "brain:ada",
            grantee: "app:ada/apps/bookmarks",
            target: "postgres.tables/ada/*",
            relation: GrantRelations.Run);
        var store = os.Fake<InMemoryGrantStore>();
        store.Add(tableGrant);
        store.Add(indexGrant);

        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(tableId);
            await table.Define(Schema);
            var changed = await table.Upsert([new("id", "\"1\"")], [new("note", "\"admitted\"")]);
            Assert.True(changed);
        }

        // Revoking the grant immediately denies subsequent calls on both runtimes.
        store.Revoke(tableGrant.Id);

        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(tableId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Read([new("id", "\"1\"")]));
        }
    }
}
