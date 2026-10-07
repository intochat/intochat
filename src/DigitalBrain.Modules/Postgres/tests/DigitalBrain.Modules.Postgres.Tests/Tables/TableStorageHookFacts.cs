using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Postgres.Tests.Tables;

public sealed class TableStorageHookFacts
{
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);
    private static string Brain => BrainScope.Create("alice", "brain").Id;
    private static string App => IApp.Key(Brain, "a");

    [Fact]
    public async Task FirstBindingTablesIsRefusedOnceTheAppOwnsSharedTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        StampApp();
        await brain.AuthorizeCallerAsync();
        await brain.Get<ITable>("owned").Define(Definition);

        StampUser();
        var hook = Hook(brain);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => hook.RequireRebindable(Brain, App, "tables", ct));
        Assert.Equal("The tables slot cannot be rebound while this app owns tables.", refusal.Message);
        await hook.RequireRebindable(Brain, App, "db", ct);
    }

    [Fact]
    public async Task BindingTablesIsAllowedBeforeAnyTableExists()
    {
        await using var brain = await Start();
        StampUser();
        await Hook(brain).RequireRebindable(Brain, App, "tables", TestContext.Current.CancellationToken);
    }

    private static IAppStorageHook Hook(ModuleBrain brain)
        => brain.SiloServices.GetServices<IAppStorageHook>().OfType<TableStorageHook>().Single();

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

    private static Task<ModuleBrain> Start()
        => ModuleTest.Create().WithModule<PostgresModule>().ConfigureSilo(silo =>
        {
            silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader";
            silo.Services.AddSingleton<ITableStore>(new PostgresWriteTableFacts.MemoryTables());
            silo.Services.AddSingleton<ITablesSlot>(new NoSlot());
        }).StartAsync(TestContext.Current.CancellationToken);

    private sealed class NoSlot : ITablesSlot
    {
        public Task<string?> BoundConnection(TableOwner owner) => Task.FromResult<string?>(null);
    }
}
