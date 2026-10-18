using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Postgres;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Postgres.Tables.Signals;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration;

public sealed class PostgresWriteTableFacts
{
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);
    private static TableValue[] Key(string id = "one") => [new("id", JsonSerializer.Serialize(id))];
    private static TableValue[] Values(string value) => [new("value", JsonSerializer.Serialize(value))];

    [Trait("host", "own")]
    [Fact]
    public async Task TeardownReclaimsPendingDdlAtItsPinnedPlacementAfterThePolicyChangesAndRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new MemoryTables { LoseDefineResponse = true };
        var placements = new ChangingPlacements();
        await using var brain = await Builder("Host=localhost;Database=sample;Username=reader", services =>
        {
            services.AddSingleton<ITableStore>(provider);
            services.AddSingleton<ITablePlacements>(placements);
        }).StartAsync(ct);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("pending-ddl");
        await Assert.ThrowsAsync<IOException>(() => table.Define(Definition));
        placements.Placement = TablePlacement.BrainDatabase("replacement");
        await brain.DeactivateAsync(table, ct);

        var owner = brain.Get<IPostgresLifecycleTestApp>("pending-install");
        await owner.Uninstall("app");
        await owner.Uninstall("app");

        Assert.Equal((TablePlacement.BrainDatabase("original"), TablePolicy.PhysicalName(new TableOwner(brain.Owner.BrainId, "app").Key, "pending-ddl")), Assert.Single(provider.Drops));
        Assert.Equal(1, placements.Resolutions);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppCallerWithoutAnAppIdCannotDefineOrBindATable()
    {
        var provider = new MemoryTables();
        await using var brain = await Start(provider);
        var table = brain.Get<ITable>("unbound");
        using (brain.Owner.AsApp(""))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Define(Definition));
        }
        Assert.Equal(0, provider.Definitions);

        // The refused call bound nothing: a real owner can still claim the table.
        using (brain.Owner.AsApp("app")) { await table.Define(Definition); }
        Assert.Equal(1, provider.Definitions);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UninstallLeavesAnotherInstallOfTheSameSchemaIntact()
    {
        await using var brain = await Start(new MemoryTables());
        using (brain.Owner.AsApp("app"))
        {
            var first = brain.Get<ITable>("first-install");
            await first.Define(Definition);
            await first.Upsert(Key(), Values("first"));
        }
        using var fork = brain.Owner.AsApp("fork-file");
        var forked = brain.Get<ITable>("fork-install");
        await forked.Define(Definition);
        await forked.Upsert(Key(), Values("fork"));

        await brain.Get<IPostgresLifecycleTestApp>("first-app").Uninstall("app");

        Assert.Equal("\"fork\"", (await forked.Read(Key()))!.Single(value => value.Column == "value").Json);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RetirementCannotDeadlockARegistrationAlreadyInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var race = new RetirementRace();
        await using var brain = await Builder("Host=localhost;Database=sample;Username=reader", services => services.AddSingleton<ITableStore>(new MemoryTables()))
            // Storage-fault injection: the race holds RequireOpen open and reports when retirement is persisted.
            // The replaced cluster storage is wrapped by the kernel after ConfigureServices.
            .ConfigureServices(services =>
            {
                services.AddSingleton<IIncomingGrainCallFilter>(race);
                services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage, race);
            }).StartAsync(ct);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("race");
        await table.Define(Definition);
        race.HoldOpen = true;
        var defining = table.Define(Definition);
        await race.Open.Task.WaitAsync(ct);
        var retiring = brain.Get<IPostgresLifecycleTestApp>("race-app").Uninstall("app");
        await race.Closed.Task.WaitAsync(ct);
        race.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => defining.WaitAsync(ct));
        await retiring.WaitAsync(ct);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TeardownTwiceAndReplayAfterRestartLeaveReinstallationEmptyAndRedefinable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new MemoryTables());
        var table = brain.Get<ITable>("lifecycle");
        var owner = brain.Get<IPostgresLifecycleTestApp>("install");
        using (brain.Owner.AsApp("app"))
        {
            await table.Define(Definition);
            await table.Upsert(Key(), Values("old"));
            await owner.Uninstall("app");
            await owner.Uninstall("app");
            await brain.DeactivateAsync(table, ct);
            await brain.DeactivateAsync(Index(brain, "app"), ct);
            await owner.Uninstall("app");
        }
        using var reinstalled = brain.Owner.AsApp("new-file");
        await table.Define(new([new("id", "text"), new("fresh", "boolean")], ["id"]));
        Assert.Empty(await table.Page());
        await table.Upsert(Key(), [new("fresh", "true")]);
        await owner.Uninstall("app");
        Assert.Single(await table.Page());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppDeletedAndReinstalledCanDefineItsTablesAgain()
    {
        await using var brain = await Start(new MemoryTables());
        var table = brain.Get<ITable>("memories");
        var owner = brain.Get<IPostgresLifecycleTestApp>("reinstall");
        using var app = brain.Owner.AsApp("app");
        await table.Define(Definition);
        await owner.Uninstall("app");
        await Assert.ThrowsAsync<InvalidOperationException>(() => table.Define(Definition));

        await owner.Reinstall("app");
        await table.Define(Definition);
        await table.Upsert(Key(), Values("new"));
        Assert.Single(await table.Page());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DeleteStillDropsTheOldAppsTables()
    {
        var provider = new MemoryTables();
        await using var brain = await Start(provider);
        var table = brain.Get<ITable>("memories");
        var owner = brain.Get<IPostgresLifecycleTestApp>("redelete");
        using var app = brain.Owner.AsApp("app");
        await table.Define(Definition);
        await table.Upsert(Key(), Values("old"));
        await owner.Uninstall("app");

        await owner.Reinstall("app");
        await table.Define(Definition);
        Assert.Empty(await table.Page());
        await owner.Uninstall("app");
        Assert.Equal(2, provider.Drops.Count);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AppsCannotReopenRetiredStorage()
    {
        await using var brain = await Start(new MemoryTables());
        using var app = brain.Owner.AsApp("app");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Index(brain, "app").Reopen);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task EveryOperationOfAPlatformTableRunsAtItsPinnedSharedPlacement()
    {
        var provider = new MemoryTables();
        await using var brain = await Start(provider);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("shared");
        await table.Define(Definition);
        await table.Upsert(Key(), Values("one"));
        await brain.Get<IPostgresLifecycleTestApp>("shared-install").Uninstall("app");

        Assert.Equal([("Define", TablePlacement.Shared), ("Upsert", TablePlacement.Shared), ("Drop", TablePlacement.Shared)], provider.Placements);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AppsCannotRequestStorageTeardown()
    {
        await using var brain = await Start(new MemoryTables());
        using var app = brain.Owner.AsApp("app");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Index(brain, "app").Retire);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DefiningTheSameSchemaIsANoopAndSurvivesReactivation()
    {
        var provider = new MemoryTables();
        await using var brain = await Start(provider);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("definitions");
        var accepted = await table.Define(Definition);
        Assert.Equal(1, accepted.Revision);
        Assert.Equal(accepted.Table, (await table.Define(new(Definition.Columns.Reverse().ToArray(), ["id"]))).Table);
        await brain.DeactivateAsync(table, TestContext.Current.CancellationToken);
        Assert.Equal(accepted.Table, (await table.Define(Definition)).Table);
        Assert.Equal(1, provider.Definitions);
        var error = await Assert.ThrowsAsync<PostgresQueryException>(() => table.Define(new([new("id", "text"), new("value", "number")], ["id"])));
        Assert.Contains("Incompatible", error.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InvalidSchemasAndValuesAreRefusedBeforeTheProviderRuns()
    {
        var provider = new MemoryTables();
        await using var brain = await Start(provider);
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("invalid");
        foreach (var name in new[] { "bad-name", "x; DROP TABLE x", "é", "1name", new string('a', 64), "" })
        { await Assert.ThrowsAsync<PostgresQueryException>(() => table.Define(new([new(name, "text")], [name]))); }
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Define(new([new("id", "varchar")], ["id"])));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Define(new([new("id", "text"), new("id", "text")], ["id"])));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Define(new([new("id", "text")], ["missing"])));
        Assert.Equal(0, provider.Definitions);
        await table.Define(Definition);
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Upsert(Key(), [new("value", "42")]));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Upsert([new("id", "null")], Values("test")));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Upsert(Key(), [new("value", "not json")]));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Upsert(Key(), []));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Page(-1, 10));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Page(0, 1001));
        await Assert.ThrowsAsync<PostgresQueryException>(() => table.Page(0, 0));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpsertsUpdateOneKeyAndIdenticalRetriesAreNoOps()
    {
        await using var brain = await Start(new MemoryTables());
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("rows");
        await table.Define(Definition);
        await ExerciseRows(table);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnotherAppCannotUnwatchAnOwnedTable()
    {
        await using var brain = await Start(new MemoryTables());
        var table = brain.Get<ITable>("watched");
        using (brain.Owner.AsApp("app")) { await table.Define(Definition); }

        using var other = brain.Owner.AsApp("other-app");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Unwatch(null!));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OtherBrainsAndAppsCannotReadOrClobberAnOwnedTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new MemoryTables());
        using var bob = await brain.PersonAsync("bob", ct);
        var first = brain.Get<ITable>("first");
        string physical;
        using (brain.Owner.AsApp("app"))
        {
            physical = (await first.Define(Definition)).Table;
            await first.Upsert(Key(), Values("first"));
        }
        foreach (var (person, appId) in new[] { (bob, "app"), (brain.Owner, "other-app") })
        {
            using var app = person.AsApp(appId);
            var second = brain.Get<ITable>(person.BrainId + appId);
            Assert.NotEqual(physical, (await second.Define(Definition)).Table);
            Assert.Null(await second.Read(Key()));
            await second.Upsert(Key(), Values("second"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Define(Definition));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Read(Key()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Page());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Upsert(Key(), Values("clobber")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Delete(Key()));
        }
        using (brain.Owner.AsApp("app"))
        {
            Assert.Equal("\"first\"", (await first.Read(Key()))!.Single(v => v.Column == "value").Json);
        }
        await Assert.ThrowsAsync<UntrustedCallerException>(() => first.Read(Key()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RowSignalsCarryTheKeyAndTheKernelStampedPublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new MemoryTables());
        using var app = brain.Owner.AsApp("app");
        var table = brain.Get<ITable>("signals");
        await using var defined = await brain.Observe<TableDefined>(table, ct);
        await using var upserted = await brain.Observe<RowUpserted>(table, ct);
        await using var deleted = await brain.Observe<RowDeleted>(table, ct);

        var accepted = await table.Define(Definition);
        Assert.Equal(accepted.Table, (await defined.NextAsync(ct: ct)).Table);

        await table.Upsert(Key(), Values("payload"));
        var row = await upserted.NextAsync(ct: ct);
        Assert.Equal(table.GetGrainId().ToString(), row.Publisher);
        Assert.Equal(accepted.Table, row.Table);
        Assert.Equal(Key(), row.Key);

        await table.Delete(Key());
        Assert.Equal(table.GetGrainId().ToString(), (await deleted.NextAsync(ct: ct)).Publisher);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CustomerResearchCanBeWrittenUsingOnlyTheNeuronContract()
    {
        await using var brain = await Start(new MemoryTables());
        using var app = brain.Owner.AsApp("app");
        await Research(brain.Get<ITable>("research-results"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LiveTablesAreParameterizedIdempotentAndVisibleThroughTheReadDoor()
    {
        var connection = Environment.GetEnvironmentVariable("INTOCHATOS_POSTGRES_TEST_CONNECTION")
            ?? Environment.GetEnvironmentVariable("CUSTOMER_RESEARCH_TEST_POSTGRES");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Set INTOCHATOS_POSTGRES_TEST_CONNECTION or CUSTOMER_RESEARCH_TEST_POSTGRES to run live table writes.");
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(null, connection!);
        using var bob = await brain.PersonAsync("bob", ct);
        var table = brain.Get<ITable>("live-" + Guid.NewGuid().ToString("N"));
        var research = brain.Get<ITable>("research-" + Guid.NewGuid().ToString("N"));
        AcceptedTable accepted;
        using (brain.Owner.AsApp("app")) { accepted = await table.Define(Definition); }
        string? researchName = null;
        string? otherName = null;
        try
        {
            using (brain.Owner.AsApp("app"))
            {
                await ExerciseRows(table);
                await table.Upsert(Key(), Values("'; DROP TABLE --"));
                // Raw deployment SQL has no brain scope; an app must use its scoped table door.
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>("default").Query(new($"SELECT value FROM public.{accepted.Table}")));
            }
            // Host code, with no caller, reads the deployment's database directly.
            var result = await brain.Get<IPostgresServer>("default").Query(new($"SELECT value FROM public.{accepted.Table}"));
            Assert.Equal("'; DROP TABLE --", JsonSerializer.Deserialize<string>(Assert.Single(Assert.Single(result.Rows))));
            await Assert.ThrowsAsync<PostgresQueryException>(() => brain.Get<IPostgresServer>("default").Query(new($"DELETE FROM public.{accepted.Table}")));
            var provider = brain.Services.GetRequiredService<ITableStore>();
            await provider.DefineAsync(TablePlacement.Shared, accepted.Table, Definition, ct);
            await Assert.ThrowsAsync<PostgresQueryException>(() => provider.DefineAsync(TablePlacement.Shared, accepted.Table,
                new([new("id", "text"), new("value", "number")], ["id"]), ct));
            using (bob.AsApp("app"))
            {
                var other = brain.Get<ITable>("other-" + Guid.NewGuid().ToString("N"));
                otherName = (await other.Define(Definition)).Table;
                Assert.Null(await other.Read(Key()));
                await other.Upsert(Key(), Values("other"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Read(Key()));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => table.Upsert(Key(), Values("clobber")));
            }
            using (brain.Owner.AsApp("app"))
            {
                Assert.Equal("'; DROP TABLE --", JsonSerializer.Deserialize<string>((await table.Read(Key()))!.Single(v => v.Column == "value").Json));
                researchName = await Research(research);
                await brain.DeactivateAsync(table, ct);
                Assert.Equal(accepted.Table, (await table.Define(Definition)).Table);
            }
        }
        finally
        {
            await using var source = NpgsqlDataSource.Create(connection!);
            foreach (var name in new[] { accepted.Table, researchName, otherName }.OfType<string>())
            {
                TablePolicy.Identifier(name);
                await using var cleanup = source.CreateCommand($"DROP TABLE public.\"{name}\"");
                await cleanup.ExecuteNonQueryAsync(ct);
            }
        }
    }

    // The index is a plain grain, not a neuron: only the grain factory addresses it.
    private static ITableIndex Index(ITestBrain brain, string appId)
        => brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ITableIndex>(new TableOwner(brain.Owner.BrainId, appId).Key);

    private static Task<ITestBrain> Start(ITableStore? provider, string connection = "Host=localhost;Database=sample;Username=reader")
        => Builder(connection, services => { if (provider is not null) { services.AddSingleton(provider); } })
            .StartAsync(TestContext.Current.CancellationToken);

    // Rule 4: a wire-level fake would need a loopback Postgres; the store stands in for the database.
    private static TestBrain Builder(string connection, Action<IServiceCollection> services)
        => TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", connection)
            .ConfigureServices(services);

    private static async Task ExerciseRows(ITable table)
    {
        Assert.True(await table.Upsert(Key(), Values("'; DROP TABLE --")));
        Assert.False(await table.Upsert(Key(), Values("'; DROP TABLE --")));
        Assert.Equal("'; DROP TABLE --", JsonSerializer.Deserialize<string>((await table.Read(Key()))!.Single(v => v.Column == "value").Json));
        Assert.True(await table.Upsert(Key(), Values("updated")));
        Assert.Single(await table.Page());
        Assert.Equal("\"updated\"", (await table.Read(Key()))!.Single(v => v.Column == "value").Json);
        Assert.Empty(await table.Page(1, 1));
        Assert.True(await table.Delete(Key()));
        Assert.False(await table.Delete(Key()));
        Assert.Null(await table.Read(Key()));
    }

    // The script needs only the Contracts assembly: no store, Npgsql or caller-supplied workspace.
    private static async Task<string> Research(ITable table)
    {
        var columns = new[] { "research_id", "company_name", "website", "location", "email", "phone", "industry", "summary" }
            .Select(name => new TableColumn(name, "text")).Concat([new("evidence", "jsonb"), new("updated_at", "timestamptz")]).ToArray();
        var accepted = await table.Define(new(columns, ["research_id"]));
        TableValue[] key = [new("research_id", "\"retry\"")];
        var values = columns.Where(c => c.Name != "research_id").Select(c => new TableValue(c.Name, c.Name switch
        {
            "company_name" => "\"O'Reilly; DROP TABLE customers; --\"",
            "evidence" => "[{\"Field\":\"Website\",\"Value\":\"https://example.com\",\"Url\":\"https://example.com\",\"Quote\":\"Evidence\"}]",
            "updated_at" => "\"2026-10-01T12:00:00Z\"",
            _ => "null"
        })).ToArray();
        Assert.True(await table.Upsert(key, values));
        Assert.False(await table.Upsert(key, values));
        values = values.Select(v => v.Column == "summary" ? new TableValue("summary", "\"Updated\"") : v).ToArray();
        Assert.True(await table.Upsert(key, values));
        var row = (await table.Read(key))!;
        Assert.Equal("\"Updated\"", row.Single(v => v.Column == "summary").Json);
        using var evidence = JsonDocument.Parse(row.Single(v => v.Column == "evidence").Json);
        Assert.Equal("Evidence", evidence.RootElement[0].GetProperty("Quote").GetString());
        return accepted.Table;
    }

    public sealed class MemoryTables : ITableStore
    {
        private readonly Dictionary<string, Dictionary<string, TableValue[]>> tables = [];
        public int Definitions { get; private set; }
        public bool LoseDefineResponse { get; set; }
        public List<(TablePlacement Placement, string Table)> Drops { get; } = [];
        public List<(string Operation, TablePlacement Placement)> Placements { get; } = [];
        public Task DropAsync(TablePlacement placement, string table, CancellationToken ct)
        { Drops.Add((placement, table)); Placements.Add(("Drop", placement)); tables.Remove(table); return Task.CompletedTask; }
        public Task DefineAsync(TablePlacement placement, string table, TableDefinition definition, CancellationToken ct)
        {
            Placements.Add(("Define", placement));
            Definitions++;
            tables.TryAdd(table, []);
            if (LoseDefineResponse) { LoseDefineResponse = false; throw new IOException("Lost the response after DDL."); }
            return Task.CompletedTask;
        }
        private static string RowKey(TableValue[] key) => JsonSerializer.Serialize(key);
        public Task<bool> UpsertAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, TableValue[] values, CancellationToken ct)
        {
            Placements.Add(("Upsert", placement));
            var row = key.Concat(values).ToArray();
            var changed = !tables[table].TryGetValue(RowKey(key), out var previous) || !row.SequenceEqual(previous);
            tables[table][RowKey(key)] = row;
            return Task.FromResult(changed);
        }
        public Task<bool> DeleteAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
            => Task.FromResult(tables[table].Remove(RowKey(key)));
        public Task<TableValue[]?> ReadAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
            => Task.FromResult(tables[table].GetValueOrDefault(RowKey(key)));
        public Task<TableValue[][]> PageAsync(TablePlacement placement, string table, TableDefinition definition, int offset, int limit, CancellationToken ct)
            => Task.FromResult(tables[table].OrderBy(p => p.Key, StringComparer.Ordinal).Skip(offset).Take(limit).Select(p => p.Value).ToArray());
    }

    private sealed class ChangingPlacements : ITablePlacements
    {
        public TablePlacement Placement { get; set; } = TablePlacement.BrainDatabase("original");
        public int Resolutions { get; private set; }
        public Task<TablePlacement> Resolve(TableOwner owner, CancellationToken ct)
        { Resolutions++; return Task.FromResult(Placement); }
    }

    private sealed class RetirementRace : IIncomingGrainCallFilter, IGrainStorage
    {
        private readonly Dictionary<(string, GrainId), object?> saved = [];
        public bool HoldOpen { get; set; }
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            await context.Invoke();
            if (HoldOpen && context.InterfaceMethod.DeclaringType == typeof(ITableIndex)
                && context.InterfaceMethod.Name == nameof(ITableIndex.RequireOpen))
            {
                HoldOpen = false;
                Open.TrySetResult();
                await Release.Task;
            }
        }
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (saved.TryGetValue((name, id), out var value)) { state.State = (T)value!; state.RecordExists = true; }
            return Task.CompletedTask;
        }
        public Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            saved[(name, id)] = state.State;
            state.RecordExists = true;
            if (state.State is TableIndexState { Retired: true }) { Closed.TrySetResult(); }
            return Task.CompletedTask;
        }
        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state) => throw new NotSupportedException();
    }
}

[IntoChat.OS.Public]
public interface IPostgresLifecycleTestApp : IntoChat.OS.INeuron
{
    Task Uninstall(string appId);
    Task Reinstall(string appId);
}

[GrainType(IntoChat.OS.Framework.AppGrainType.Name)]
public sealed class PostgresLifecycleTestApp : IntoChat.OS.Framework.Neuron, IPostgresLifecycleTestApp
{
    public Task Uninstall(string appId)
        => GrainFactory.GetGrain<ITableIndex>(new TableOwner(BrainScope.CurrentId(), appId).Key).Retire();

    public Task Reinstall(string appId)
        => GrainFactory.GetGrain<ITableIndex>(new TableOwner(BrainScope.CurrentId(), appId).Key).Reopen();
}
