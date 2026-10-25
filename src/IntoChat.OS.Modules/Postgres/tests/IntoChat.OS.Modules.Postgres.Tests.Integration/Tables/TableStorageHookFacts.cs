using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration.Tables;

public sealed class TableStorageHookFacts
{
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);

    [Trait("host", "own")]
    [Fact]
    public async Task RebindingTablesIsRefusedOnceTheAppOwnsSharedTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var owner = brain.Owner;
        var app = IApp.Key(owner.BrainId, "a");
        using (owner.AsApp(app)) { await brain.Get<ITable>("owned").Define(Definition); }

        using var caller = owner.AsCaller();
        var hook = Hook(brain);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => hook.RequireRebindable(owner.BrainId, app, "tables", ct));
        Assert.Equal("The tables slot cannot be rebound while this app owns tables.", refusal.Message);
        await hook.RequireRebindable(owner.BrainId, app, "db", ct);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BindingTablesIsAllowedBeforeAnyTableExists()
    {
        await using var brain = await Start();
        using var caller = brain.Owner.AsCaller();
        await Hook(brain).RequireRebindable(brain.Owner.BrainId, IApp.Key(brain.Owner.BrainId, "a"), "tables", TestContext.Current.CancellationToken);
    }

    // The module's hook singleton the facts assert on.
    private static IAppStorageHook Hook(ITestBrain brain)
        => brain.Services.GetServices<IAppStorageHook>().OfType<TableStorageHook>().Single();

    private static Task<ITestBrain> Start()
        => TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            // Rule 4: a wire-level fake would need a loopback Postgres; the store stands in for the database,
            // and the slot for an app with no tables binding.
            .ConfigureServices(services =>
            {
                services.AddSingleton<ITableStore>(new PostgresWriteTableFacts.MemoryTables());
                services.AddSingleton<ITablesSlot>(new NoSlot());
            }).StartAsync(TestContext.Current.CancellationToken);

    private sealed class NoSlot : ITablesSlot
    {
        public Task<string?> BoundConnection(TableOwner owner) => Task.FromResult<string?>(null);
    }
}
