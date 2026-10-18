using IntoChat.OS.Postgres;
using IntoChat.OS.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration.Tables;

public sealed class TableLifecycleGuardFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AnIndexOfAnotherBrainRefusesEveryCallerAndAnAppCannotReadItsOwnIndex()
    {
        await using var brain = await TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            // Rule 4: a wire-level fake would need a loopback Postgres; the store stands in for the database.
            .ConfigureServices(services => services.AddSingleton<ITableStore>(new PostgresWriteTableFacts.MemoryTables()))
            .StartAsync(TestContext.Current.CancellationToken);
        // The index is a plain grain, not a neuron: only the grain factory addresses it.
        var grains = brain.Services.GetRequiredService<IGrainFactory>();
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(grains.GetGrain<ITableIndex>(new TableOwner("other-brain", "install").Key).ReadTables);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(grains.GetGrain<ITableIndex>("not a key").ReadTables);
        }
        using (brain.Owner.AsApp("install"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(grains.GetGrain<ITableIndex>(new TableOwner(brain.Owner.BrainId, "install").Key).ReadTables);
        }
    }
}
