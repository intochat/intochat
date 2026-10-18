using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Postgres.Tests.Integration.Tables;

public sealed class ConnectionPlacementFacts
{
    private const string UserDatabase = "Host=user-db;Database=mine;Username=owner";
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);
    private static readonly TableValue[] Key = [new("id", "\"one\"")];
    private static readonly TableValue[] Values = [new("value", "\"v\"")];

    [Trait("host", "own")]
    [Fact]
    public async Task ABoundTablesSlotPlacesTablesInThatConnection()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var connection = new ConnectionTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, connection, slot);
        await Connect(brain, "c1");
        slot.Bind(App(brain), "c1");

        using var app = brain.Owner.AsApp(App(brain));
        var table = brain.Get<ITable>("placed");
        await table.Define(Definition);
        await table.Upsert(Key, Values);

        Assert.Equal([UserDatabase, UserDatabase], connection.ConnectionStrings);
        Assert.Equal([("Define", TablePlacement.Shared), ("Upsert", TablePlacement.Shared)], connection.Tables.Placements);
        Assert.Empty(shared.Placements);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnboundSlotFallsBackToThePolicy()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var connection = new ConnectionTables();
        await using var brain = await Start(shared, connection, new BoundSlot());
        using var app = brain.Owner.AsApp(App(brain));
        await brain.Get<ITable>("fallback").Define(Definition);

        Assert.Equal([("Define", TablePlacement.Shared)], shared.Placements);
        Assert.Empty(connection.ConnectionStrings);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnavailableBoundConnectionRefusesWithServiceUnavailable()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, new ConnectionTables(), slot);
        var key = await Connect(brain, "c1");
        slot.Bind(App(brain), "c1");
        var table = brain.Get<ITable>("revoked");
        using (brain.Owner.AsApp(App(brain))) { await table.Define(Definition); }

        using (brain.Owner.AsCaller()) { await brain.Get<IConnection>(key).Disconnect(); }

        using (brain.Owner.AsApp(App(brain))) { await Assert.ThrowsAnyAsync<ServiceUnavailableException>(() => table.Upsert(Key, Values)); }
        Assert.Empty(shared.Placements);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UninstallStillReclaimsTheAppsOtherTablesWhenItsBoundConnectionIsGone()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, new ConnectionTables(), slot);
        var key = await Connect(brain, "c1");
        slot.Bind(App(brain), "c1");
        AcceptedTable kept;
        using (brain.Owner.AsApp(App(brain)))
        {
            await brain.Get<ITable>("in-connection").Define(Definition);
            slot.Unbind(App(brain));
            kept = await brain.Get<ITable>("in-shared").Define(Definition);
        }

        using (brain.Owner.AsCaller())
        {
            await brain.Get<IConnection>(key).Disconnect();
            await brain.Get<IPostgresLifecycleTestApp>("uninstall").Uninstall(App(brain));
        }

        Assert.Equal([(TablePlacement.Shared, kept.Table)], shared.Drops);
    }

    private static string App(ITestBrain brain) => IApp.Key(brain.Owner.BrainId, "a");

    private static async Task<string> Connect(ITestBrain brain, string id)
    {
        var key = ConnectionKeys.For(brain.Owner.BrainId, "postgres", id);
        using var owner = brain.Owner.AsCaller();
        await brain.Get<IConnection>(key).Connect("connection-string", UserDatabase);
        return key;
    }

    private static Task<ITestBrain> Start(ITableStore shared, ConnectionTables connection, BoundSlot slot)
        => TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            // Rule 4: a wire-level fake would need a loopback Postgres; the stores stand in for the databases,
            // and the slot stands in for an installed app's tables binding.
            .ConfigureServices(services =>
            {
                services.AddSingleton(shared);
                services.AddSingleton<IConnectionTableStores>(connection);
                services.AddSingleton<ITablesSlot>(slot);
            }).StartAsync(TestContext.Current.CancellationToken);

    private sealed class BoundSlot : ITablesSlot
    {
        private readonly Dictionary<string, string> bound = [];
        public void Bind(string app, string connection) => bound[app] = connection;
        public void Unbind(string app) => bound.Remove(app);
        public Task<string?> BoundConnection(TableOwner owner)
            => Task.FromResult(bound.TryGetValue(owner.AppId, out var id) ? ConnectionKeys.For(owner.BrainId, "postgres", id) : null);
    }

    private sealed class ConnectionTables : IConnectionTableStores
    {
        public PostgresWriteTableFacts.MemoryTables Tables { get; } = new();
        public List<string> ConnectionStrings { get; } = [];
        public ITableStore ForConnectionString(string connectionString)
        {
            ConnectionStrings.Add(connectionString);
            return Tables;
        }
    }
}
