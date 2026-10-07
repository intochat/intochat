using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Postgres.Tests.Tables;

public sealed class ConnectionPlacementFacts
{
    private const string UserDatabase = "Host=user-db;Database=mine;Username=owner";
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);
    private static readonly TableValue[] Key = [new("id", "\"one\"")];
    private static readonly TableValue[] Values = [new("value", "\"v\"")];

    [Fact]
    public async Task ABoundTablesSlotPlacesTablesInThatConnection()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var connection = new ConnectionTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, connection, slot);
        await Connect(brain, "c1");
        slot.Bind(App, "c1");

        StampApp();
        await brain.AuthorizeCallerAsync();
        var table = brain.Get<ITable>("placed");
        await table.Define(Definition);
        await table.Upsert(Key, Values);

        Assert.Equal([UserDatabase, UserDatabase], connection.ConnectionStrings);
        Assert.Equal([("Define", TablePlacement.Shared), ("Upsert", TablePlacement.Shared)], connection.Tables.Placements);
        Assert.Empty(shared.Placements);
    }

    [Fact]
    public async Task AnUnboundSlotFallsBackToThePolicy()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var connection = new ConnectionTables();
        await using var brain = await Start(shared, connection, new BoundSlot());
        StampApp();
        await brain.AuthorizeCallerAsync();
        await brain.Get<ITable>("fallback").Define(Definition);

        Assert.Equal([("Define", TablePlacement.Shared)], shared.Placements);
        Assert.Empty(connection.ConnectionStrings);
    }

    [Fact]
    public async Task AnUnavailableBoundConnectionRefusesWithServiceUnavailable()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, new ConnectionTables(), slot);
        await Connect(brain, "c1");
        slot.Bind(App, "c1");
        StampApp();
        await brain.AuthorizeCallerAsync();
        var table = brain.Get<ITable>("revoked");
        await table.Define(Definition);

        StampUser();
        await brain.Get<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), "postgres", "c1")).Disconnect();
        StampApp();
        await brain.AuthorizeCallerAsync();

        await Assert.ThrowsAnyAsync<ServiceUnavailableException>(() => table.Upsert(Key, Values));
        Assert.Empty(shared.Placements);
    }

    [Fact]
    public async Task UninstallStillReclaimsTheAppsOtherTablesWhenItsBoundConnectionIsGone()
    {
        var shared = new PostgresWriteTableFacts.MemoryTables();
        var slot = new BoundSlot();
        await using var brain = await Start(shared, new ConnectionTables(), slot);
        await Connect(brain, "c1");
        slot.Bind(App, "c1");
        StampApp();
        await brain.AuthorizeCallerAsync();
        await brain.Get<ITable>("in-connection").Define(Definition);
        slot.Unbind(App);
        var kept = await brain.Get<ITable>("in-shared").Define(Definition);

        StampUser();
        await brain.Get<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), "postgres", "c1")).Disconnect();
        await brain.Get<IPostgresLifecycleTestApp>("uninstall").Uninstall("brain", App);

        Assert.Equal([(TablePlacement.Shared, kept.Table)], shared.Drops);
    }

    private static string App => IApp.Key(BrainScope.Create("alice", "brain").Id, "a");

    private static async Task Connect(ModuleBrain brain, string id)
    {
        StampUser();
        await brain.AuthorizeCallerAsync();
        await brain.Get<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), "postgres", id)).Connect("connection-string", UserDatabase);
    }

    private static void StampUser()
        => CallerContextStamper.Stamp(new() { PrincipalId = "alice", AccountId = "alice", BrainId = "brain", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    private static void StampApp()
        => CallerContextStamper.Stamp(new()
        {
            PrincipalId = "alice",
            AccountId = "alice",
            BrainId = "brain",
            Kind = CallerKind.App,
            StampedBy = TrustedEdge.AppProxy,
            AppId = App
        });

    private static Task<ModuleBrain> Start(ITableStore shared, ConnectionTables connection, BoundSlot slot)
        => ModuleTest.Create().WithModule<PostgresModule>().ConfigureSilo(silo =>
        {
            silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader";
            silo.Services.AddSingleton(shared);
            silo.Services.AddSingleton<IConnectionTableStores>(connection);
            silo.Services.AddSingleton<ITablesSlot>(slot);
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
