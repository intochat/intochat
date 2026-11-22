using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;

namespace IntoChat.OS.Tests.Integration.Modules.Postgres;

public sealed class TypedTableFacts
{
    public sealed record CustomerProfile(
        [property: TableKey] string CustomerId,
        string Name,
        string? Company,
        int Seats,
        bool Active,
        DateTimeOffset UpdatedAt,
        string[] Tags);

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TypedTableDefinesUpsertsReadsPagesAndDeletesCustomerProfiles(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, f => f.Postgres(), TestContext.Current.CancellationToken);
        var ada = os.Person("ada");
        var id = new NeuronId(ada.Brain, TableNeuron.GrainType, "crm/customers");

        using (ada.EnterAsApp("customer-researcher"))
        {
            ITable<CustomerProfile> table = ada.Get<ITable>(id).Typed<CustomerProfile>();
            var accepted = await table.Define();

            var t1 = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 11, 22, 11, 30, 0, TimeSpan.Zero);
            var alice = new CustomerProfile("cust-1", "Alice", "Acme Corp", 25, true, t1, ["enterprise", "beta"]);
            var bob = new CustomerProfile("cust-2", "Bob", null, 5, false, t2, ["starter"]);

            Assert.True(await table.Upsert(alice));
            Assert.True(await table.Upsert(bob));

            var readAlice = await table.Read("cust-1");
            Assert.NotNull(readAlice);
            Assert.Equal("cust-1", readAlice.CustomerId);
            Assert.Equal("Alice", readAlice.Name);
            Assert.Equal("Acme Corp", readAlice.Company);
            Assert.Equal(25, readAlice.Seats);
            Assert.True(readAlice.Active);
            Assert.Equal(t1, readAlice.UpdatedAt);
            Assert.Equal(["enterprise", "beta"], readAlice.Tags);

            var readBob = await table.Read("cust-2");
            Assert.NotNull(readBob);
            Assert.Null(readBob.Company);
            Assert.False(readBob.Active);

            // Update existing row
            var updatedAlice = alice with { Seats = 40, Tags = ["enterprise", "annual"] };
            Assert.True(await table.Upsert(updatedAlice));
            var reReadAlice = await table.Read("cust-1");
            Assert.NotNull(reReadAlice);
            Assert.Equal(40, reReadAlice.Seats);
            Assert.Equal(["enterprise", "annual"], reReadAlice.Tags);

            // Page with skip & take
            var all = await table.Page(skip: 0, take: 50);
            Assert.Equal(2, all.Count);
            var secondPage = await table.Page(skip: 1, take: 1);
            Assert.Single(secondPage);

            // Verify FakeTableStore persistence
            var store = os.Fake<FakeTableStore>();
            Assert.Contains(accepted.Table, store.Tables);
            Assert.Equal(2, store.Rows(accepted.Table).Length);

            // Delete & confirm gone
            Assert.True(await table.Delete("cust-1"));
            Assert.Null(await table.Read("cust-1"));
            Assert.Single(await table.Page());
        }
    }
}
