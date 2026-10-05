using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

public sealed class PostgresInventoryFacts
{
    private static readonly TableDefinition Definition = new([new("id", "text")], ["id"]);
    [Fact]
    public async Task InventoryIntersectsAppOwnershipWithCensusWithoutCrossingBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PostgresModule>().WithModule<DigitalBrain.Apps.AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=sample;Username=reader";
                silo.Services.AddSingleton<IPostgresTableProvider>(new MemoryTables());
                silo.Services.AddSingleton<ICapacity>(new ChangingCapacity());
            }).StartAsync(ct);
        foreach (var name in new[] { "one", "two" })
        {
            CallerContextStamper.Stamp(new()
            {
                PrincipalId = "owner",
                AccountId = "account",
                BrainId = name,
                Kind = CallerKind.User,
                StampedBy = TrustedEdge.AuthenticatedHttp
            });
            await brain.AuthorizeCallerAsync();
            var package = DigitalBrain.Apps.PackageId.Create("owner", name);
            var revision = await brain.Get<DigitalBrain.Apps.IPackage>(package.ToString()).Commit(new(Guid.NewGuid(), null,
                new(new(name, "Inventory", [], [], Runtime: "prompt"), ""), "Initial"));
            var key = BrainScope.CurrentId() + "/packages/" + package;
            await brain.Get<DigitalBrain.Apps.IApp>(key).Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));
            CallerContextStamper.Stamp(CallerContextStamper.Require() with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy, AppId = key });
            await brain.AuthorizeCallerAsync();
            await brain.Get<IPostgresTable>("inventory-" + name).Define(Definition);
        }
        var inventory = brain.SiloServices.GetServices<DigitalBrain.Framework.IRegistryInventoryProvider>().Single(p => p.Domain == "postgres");
        foreach (var name in new[] { "one", "two" })
        {
            CallerContextStamper.Stamp(new()
            {
                PrincipalId = "owner",
                AccountId = "account",
                BrainId = name,
                Kind = CallerKind.User,
                StampedBy = TrustedEdge.AuthenticatedHttp
            });
            Assert.Equal("inventory-" + name, Assert.Single(await inventory.Persisted(null, ct)).Key);
            Assert.Equal("inventory-" + name, Assert.Single(await inventory.Active(null, ct)).Key);
            await brain.DeactivateAsync(brain.Get<IPostgresTable>("inventory-" + name), ct);
            Assert.Empty(await inventory.Active(null, ct));
            Assert.Empty(await inventory.Persisted("other", ct));
        }
    }

    private sealed class MemoryTables : IPostgresTableProvider
    {
        public Task DefineAsync(string origin, string table, TableDefinition definition, CancellationToken ct) => Task.CompletedTask;
        public Task DropAsync(string origin, string table, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> UpsertAsync(string origin, string table, TableDefinition definition, TableValue[] key, TableValue[] values, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string origin, string table, TableDefinition definition, TableValue[] key, CancellationToken ct) => throw new NotSupportedException();
        public Task<TableValue[]?> ReadAsync(string origin, string table, TableDefinition definition, TableValue[] key, CancellationToken ct) => throw new NotSupportedException();
        public Task<TableValue[][]> PageAsync(string origin, string table, TableDefinition definition, int offset, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ChangingCapacity : ICapacity
    {
        public ValueTask<ResolvedCapacity> Resolve(string kind, CapacityScope scope, CancellationToken ct = default) => ValueTask.FromResult(new ResolvedCapacity(kind, "db:test"));
        public ValueTask<ResolvedCapacity> Provision(string kind, CapacityScope scope, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
