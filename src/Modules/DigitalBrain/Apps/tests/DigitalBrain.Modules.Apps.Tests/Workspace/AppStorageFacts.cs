using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

public sealed class AppStorageFacts
{
    [Fact]
    public async Task ConfigureAndUpgradeMigrateLegacyRowsThenUninstallRetiresTheStableOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new SavedTable();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<PostgresModule>()
            .ConfigureSilo(silo =>
            {
                silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=sample;Username=reader";
                silo.Services.AddSingleton<IPostgresTableProvider>(provider);
            }).StartAsync(ct);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var package = PackageId.Parse("alice/storage-lifetime");
        var content = PackageSamples.Researcher("Research");
        var revision = await brain.Get<IPackage>(package.ToString()).Commit(new(Guid.NewGuid(), null, content, "First"));
        var app = brain.Get<IApp>(BrainScope.CurrentId() + "/apps/stable-storage-install");
        var installed = await app.Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));
        static void AsApp(string id) => CallerContextStamper.Stamp(CallerContextStamper.Require() with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy, AppId = id });
        AsApp(installed.CSharpFiles.Single());
        var table = brain.Get<IPostgresTable>("storage-lifetime-rows");
        var physical = await table.Define(new([new("id", "text"), new("value", "text")], ["id"]));
        TableValue[] key = [new("id", "\"saved\"")];
        await table.Upsert(key, [new("value", "\"original row\"")]);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        await app.Configure(new(Guid.NewGuid(), new Dictionary<string, string>()));
        AsApp(BrainScope.CurrentId() + "/apps/stable-storage-install");
        Assert.Equal("\"original row\"", (await table.Read(key))!.Single(v => v.Column == "value").Json);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var next = await brain.Get<IPackage>(package.ToString()).Commit(new(Guid.NewGuid(), revision.Id, new PackageContent(content.Manifest, "// upgraded", content.Files), "Upgrade"));
        await app.Upgrade(new(Guid.NewGuid(), new(package, next.Id)));
        AsApp(BrainScope.CurrentId() + "/apps/stable-storage-install");
        Assert.Equal("\"original row\"", (await table.Read(key))!.Single(v => v.Column == "value").Json);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        await app.Uninstall(new(Guid.NewGuid()));
        Assert.Equal(physical.Table, Assert.Single(provider.Drops));
        Assert.Empty(provider.Row);
    }

    private sealed class SavedTable : IPostgresTableProvider
    {
        public TableValue[] Row { get; private set; } = [];
        public List<string> Drops { get; } = [];
        public Task DefineAsync(string origin, string table, TableDefinition definition, CancellationToken ct) => Task.CompletedTask;
        public Task DropAsync(string origin, string table, CancellationToken ct) { Drops.Add(table); Row = []; return Task.CompletedTask; }
        public Task<bool> UpsertAsync(string origin, string table, TableDefinition definition, TableValue[] key, TableValue[] values, CancellationToken ct)
        { Row = [.. key, .. values]; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(string origin, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
        { Row = []; return Task.FromResult(true); }
        public Task<TableValue[]?> ReadAsync(string origin, string table, TableDefinition definition, TableValue[] key, CancellationToken ct) => Task.FromResult<TableValue[]?>(Row);
        public Task<TableValue[][]> PageAsync(string origin, string table, TableDefinition definition, int offset, int limit, CancellationToken ct) => Task.FromResult<TableValue[][]>([Row]);
    }

}
