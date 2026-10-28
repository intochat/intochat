using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

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
        // The index is a plain neuron (06-08).
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(SiloNeurons.Get<ITableIndex>(brain.Services, new TableOwner("other-brain", "install").Key).ReadTables);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(SiloNeurons.Get<ITableIndex>(brain.Services, new global::DigitalBrain.NeuronId(new(brain.Owner.BrainId), TableIndexNeuron.GrainType, "not-a-key")).ReadTables);
        }
        using (brain.Owner.AsApp("install"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(SiloNeurons.Get<ITableIndex>(brain.Services, new TableOwner(brain.Owner.BrainId, "install").Key).ReadTables);
        }
    }
}
