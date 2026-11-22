using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ATableDefinedByAComponentNeuronIsOwnedByThatComponentAndSurvivesReactivation(NeuronRuntimeKind kind)
    {
        ComponentRegistry.Register(typeof(ComponentTableCallerNeuron), new ComponentId("app.customer-researcher"));

        await using var os = await JourneyOS.Start(
            kind,
            [.. Journey.SliceModules, typeof(ComponentTableCallerModule)],
            f => f.Postgres(),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var researcherId = new NeuronId(ada.Brain, "test.component-table-caller", "main");
        var tableKey = "research/notes";
        var tableId = new NeuronId(ada.Brain, TableNeuron.GrainType, tableKey);

        var physicalTable = await ada.Get<IComponentTableCaller>(researcherId)
            .DefineAndUpsert(tableKey, "\"1\"", "\"Customer insight\"");

        var expectedOwner = new TableOwner(ada.Brain.Value, "app.customer-researcher");
        var expectedPhysical = TablePolicy.PhysicalName(expectedOwner.Key, $"{ada.Brain.Value}/{tableKey}");
        Assert.Equal(expectedPhysical, physicalTable);

        // Another app cannot read the component-owned table.
        using (ada.EnterAsApp("other-app"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ada.Get<ITable>(tableId).Read([new("id", "\"1\"")]));
        }

        // Reactivate every neuron over the same state and read back through the same component neuron.
        await os.Runtime.Restart(TestContext.Current.CancellationToken);

        var title = await ada.Get<IComponentTableCaller>(researcherId).ReadTitle(tableKey, "\"1\"");
        Assert.Equal("\"Customer insight\"", title);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task WithAViewerOfferAFriendReadsAndCannotWrite(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, ConfigureFriendsAndPostgres, TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        await MakeFriends(ada, bob);

        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            await table.Define(BookmarksSchema);
            await table.Upsert([new("id", "\"1\"")], [new("title", "\"Shared\"")]);
        }

        await ada.Offers.Offer(id, bob.Account, Role.Viewer, TestContext.Current.CancellationToken);

        var row = await bob.Get<ITable>(id).Read([new("id", "\"1\"")]);
        Assert.NotNull(row);
        Assert.Equal("\"Shared\"", row.Single(v => v.Column == "title").Json);

        var page = await bob.Get<ITable>(id).Page();
        Assert.Single(page);

        var upsertError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Upsert([new("id", "\"2\"")], [new("title", "\"Nope\"")]));
        Assert.Contains("offer.read-only", upsertError.Message, StringComparison.Ordinal);

        var deleteError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Delete([new("id", "\"1\"")]));
        Assert.Contains("offer.read-only", deleteError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task WithNoOfferAFriendIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, ConfigureFriendsAndPostgres, TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        await MakeFriends(ada, bob);

        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            await table.Define(BookmarksSchema);
            await table.Upsert([new("id", "\"1\"")], [new("title", "\"Private\"")]);
        }

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Read([new("id", "\"1\"")]));
        Assert.Contains("offer.none", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARevokedOfferIsRefusedOnTheNextCall(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, ConfigureFriendsAndPostgres, TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        await MakeFriends(ada, bob);

        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            await table.Define(BookmarksSchema);
            await table.Upsert([new("id", "\"1\"")], [new("title", "\"Temporary\"")]);
        }

        await ada.Offers.Offer(id, bob.Account, Role.Viewer, TestContext.Current.CancellationToken);
        Assert.NotNull(await bob.Get<ITable>(id).Read([new("id", "\"1\"")]));

        await ada.Offers.Revoke(id, bob.Account, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Read([new("id", "\"1\"")]));
        Assert.Contains("offer.none", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnEditorOfferMayWriteButNotDefineOrRetire(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, ConfigureFriendsAndPostgres, TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        await MakeFriends(ada, bob);

        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        using (ada.EnterAsApp("bookmarks"))
        {
            await ada.Get<ITable>(id).Define(BookmarksSchema);
        }

        await ada.Offers.Offer(id, bob.Account, Role.Editor, TestContext.Current.CancellationToken);

        Assert.True(await bob.Get<ITable>(id).Upsert([new("id", "\"1\"")], [new("title", "\"From Bob\"")]));
        var row = await bob.Get<ITable>(id).Read([new("id", "\"1\"")]);
        Assert.NotNull(row);
        Assert.Equal("\"From Bob\"", row.Single(v => v.Column == "title").Json);

        Assert.True(await bob.Get<ITable>(id).Delete([new("id", "\"1\"")]));
        Assert.Null(await bob.Get<ITable>(id).Read([new("id", "\"1\"")]));

        // Define and Retire stay owner-only regardless of Editor role.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Define(BookmarksSchema));

        // ITableLifetime.Retire is [OneWayCall] on a non-[Shareable] contract: TestOS refuses synchronously,
        // while Silo refuses on the host grain without propagating the exception to the one-way caller.
        try
        {
            await bob.Get<ITableLifetime>(id).Retire(new TableOwner(ada.Brain.Value, "bookmarks").Key);
        }
        catch (UnauthorizedAccessException)
        {
            // Expected on synchronous refusal.
        }
        await os.Idle();
        Assert.True(await bob.Get<ITable>(id).Upsert([new("id", "\"2\"")], [new("title", "\"Still alive\"")]));
        var surviving = await bob.Get<ITable>(id).Read([new("id", "\"2\"")]);
        Assert.NotNull(surviving);
        Assert.Equal("\"Still alive\"", surviving.Single(v => v.Column == "title").Json);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task UnfriendingRemovesTheFriendsOffers(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, ConfigureFriendsAndPostgres, TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        await MakeFriends(ada, bob);

        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "bookmarks/items");
        using (ada.EnterAsApp("bookmarks"))
        {
            var table = ada.Get<ITable>(id);
            await table.Define(BookmarksSchema);
            await table.Upsert([new("id", "\"1\"")], [new("title", "\"Shared\"")]);
        }

        await ada.Offers.Offer(id, bob.Account, Role.Viewer, TestContext.Current.CancellationToken);
        Assert.NotNull(await bob.Get<ITable>(id).Read([new("id", "\"1\"")]));

        var adaFriends = ada.Get<IFriends>(FriendsDefaults.Id(ada.Brain.Value));
        await adaFriends.Unfriend(new AccountId(bob.Account));

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<ITable>(id).Read([new("id", "\"1\"")]));
        Assert.Contains("offer.none", error.Message, StringComparison.Ordinal);

        // Offering to an ex-friend is refused by IFriendCheck.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ada.Offers.Offer(id, bob.Account, Role.Viewer, TestContext.Current.CancellationToken));
    }

    private static void ConfigureFriendsAndPostgres(FakeSet fakes)
    {
        fakes.Postgres();
        fakes.Add<IBrainOwners, SameNameOwners>(new SameNameOwners());
        fakes.Add<ICodeBook, InMemoryCodeBook>(new InMemoryCodeBook());
        fakes.Configure(services =>
        {
            services.TryAddSingleton<IFriendCheck>(sp => new FriendsFriendCheck(sp.GetRequiredService<INeuronProxies>()));
        });
    }

    private static async Task MakeFriends(Person ada, Person bob)
    {
        var code = await ada.Get<IFriends>(FriendsDefaults.Id(ada.Brain.Value)).Mint();
        await bob.Get<IFriends>(FriendsDefaults.Id(bob.Brain.Value)).Redeem(code);
    }

    private sealed class SameNameOwners : IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(brain.Value);
    }

    private sealed class InMemoryCodeBook : ICodeBook
    {
        private readonly ConcurrentDictionary<(CodePurpose Purpose, string Hash), CodeTarget> _codes = new();

        public Task<string> Bind(Code code, CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt)
        {
            var hash = code.Value;
            _codes[(purpose, hash)] = new CodeTarget(brain, reference, expiresAt);
            return Task.FromResult(hash);
        }

        public Task<string> HashOf(Code code) => Task.FromResult(code.Value);

        public Task<CodeTarget?> Find(CodePurpose purpose, string hash)
            => Task.FromResult(_codes.TryGetValue((purpose, hash), out var target) ? target : null);

        public Task<CodeTarget?> Lookup(CodePurpose purpose, string hash) => Find(purpose, hash);

        public Task Spend(string hash)
        {
            foreach (var key in _codes.Keys.Where(k => k.Hash == hash).ToList())
            {
                _codes.TryRemove(key, out _);
            }
            return Task.CompletedTask;
        }
    }
}
