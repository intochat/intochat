using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration;

public sealed class PostgresIsolationFacts
{
    private const string Admin = "Host=localhost;Database=postgres;Username=admin;Password=admin";
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);

    [Trait("host", "own")]
    [Fact]
    public async Task UnderSharedIsolationTablesArePlacedShared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            .StartAsync(ct);
        var placements = brain.Services.GetRequiredService<ITablePlacements>();
        Assert.Equal(TablePlacement.Shared, await placements.Resolve(new TableOwner("brain-1", "behavior-x"), ct));
    }

    [Fact]
    public async Task UnderDatabasePerBrainEachBrainGetsItsOwnDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<string>();
        var databases = new BrainDatabases(new AdminSecret(Admin), (_, database, _) => { created.Add(database); return Task.CompletedTask; });
        var placements = new TablePlacements(Options.Create(new PostgresModuleOptions { Isolation = PostgresIsolation.DatabasePerBrain }), databases, new AppTablesSlot(null!));

        var one = await placements.Resolve(new TableOwner("brain-1", "behavior-x"), ct);
        var two = await placements.Resolve(new TableOwner("brain-2", "behavior-x"), ct);

        Assert.Equal(TablePlacement.BrainDatabase(BrainDatabases.DatabaseName("brain-1")), one);
        Assert.Equal(TablePlacement.BrainDatabase(BrainDatabases.DatabaseName("brain-2")), two);
        Assert.Equal(one, await placements.Resolve(new TableOwner("brain-1", "behavior-y"), ct));
        Assert.Equal(await databases.Ensure("brain-1", ct), await databases.Ensure("brain-1", ct));
        Assert.Equal([BrainDatabases.DatabaseName("brain-1"), BrainDatabases.DatabaseName("brain-2")], created);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DatabasePerBrainWithoutAnAdminConnectionFailsAtStartup()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestBrain.Create()
            .WithModule<PostgresModule, PostgresModuleOptions>(options => options.Isolation = PostgresIsolation.DatabasePerBrain)
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            .StartAsync(TestContext.Current.CancellationToken));
        var refusal = Assert.IsType<InvalidOperationException>(error.GetBaseException());
        Assert.Equal("AdminConnection is required when Postgres isolation is DatabasePerBrain.", refusal.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APinnedBrainDatabaseSurvivesAPolicyFlipAndRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var tables = new PostgresWriteTableFacts.MemoryTables();
        await using var brain = await TestBrain.Create()
            .WithModule<PostgresModule, PostgresModuleOptions>(options => options.Isolation = PostgresIsolation.DatabasePerBrain)
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            .WithIntegration(PostgresModule.Integration, "AdminConnection", Admin)
            // Rule 4: a wire-level fake would need a loopback Postgres; the store and database creator stand in for it.
            .ConfigureServices(services =>
            {
                services.AddSingleton<ITableStore>(tables);
                services.AddSingleton(new BrainDatabases(new AdminSecret(Admin), (_, _, _) => Task.CompletedTask));
            }).StartAsync(ct);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("pinned");
        await table.Define(Definition);

        // A module singleton the fact flips: the policy changes under a live brain.
        brain.Services.GetRequiredService<IOptions<PostgresModuleOptions>>().Value.Isolation = PostgresIsolation.Shared;
        await brain.DeactivateAsync(table, ct);
        await table.Upsert([new("id", "\"one\"")], [new("value", "\"v\"")]);

        var pinned = TablePlacement.BrainDatabase(BrainDatabases.DatabaseName(brain.Owner.BrainId));
        Assert.Equal([("Define", pinned), ("Upsert", pinned)], tables.Placements);
    }

    [Fact]
    public void DatabaseNamesAreStableHashedAndDistinctPerBrain()
    {
        var one = BrainDatabases.DatabaseName("brain-1");
        var two = BrainDatabases.DatabaseName("brain-2");
        Assert.Equal(one, BrainDatabases.DatabaseName("brain-1"));
        Assert.NotEqual(one, two);
        Assert.Matches("^brain_[0-9a-f]{16}$", one);
    }

    [Theory]
    [InlineData(PostgresIsolation.Shared, null)]
    [InlineData(PostgresIsolation.Shared, "Password=invalid-unused-admin-canary")]
    [InlineData(PostgresIsolation.DatabasePerBrain, "Host=localhost;Database=postgres;Username=admin")]
    public async Task StartupRequiresAdminOnlyWhenDatabasePerBrainIsSelected(PostgresIsolation isolation, string? admin)
    {
        var bindings = new StartupBindings(admin);
        await using var sources = new PostgresSourceRegistry(bindings, Options.Create(new PostgresModuleOptions { Isolation = isolation }));
        await sources.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, bindings.Releases);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Password=invalid-admin-canary")]
    public async Task DatabasePerBrainRejectsMissingOrInvalidAdminBeforeStartupCompletes(string? admin)
    {
        var bindings = new StartupBindings(admin);
        await using var sources = new PostgresSourceRegistry(bindings, Options.Create(new PostgresModuleOptions { Isolation = PostgresIsolation.DatabasePerBrain }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sources.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, bindings.Releases);
        Assert.DoesNotContain("invalid-admin-canary", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class AdminSecret(string admin) : IIntegrationHandle
    {
        public string IntegrationId => "postgres";
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => field == "AdminConnection" ? Task.FromResult(admin) : throw new NotSupportedException();
    }

    private sealed class StartupBindings(string? admin) : IModuleIntegrations<PostgresModule>, IIntegrationHandle
    {
        public string IntegrationId => "postgres";
        public int Releases { get; private set; }
        public IIntegrationHandle Get(string integrationId) { Assert.Equal(IntegrationId, integrationId); return this; }
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            var values = new Dictionary<string, string> { ["ConnectionString"] = "Host=localhost;Database=platform;Username=reader" };
            if (admin is not null) { values["AdminConnection"] = admin; }
            return new() { Snapshot = await ReadAsync(cancellationToken), Values = values };
        }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
