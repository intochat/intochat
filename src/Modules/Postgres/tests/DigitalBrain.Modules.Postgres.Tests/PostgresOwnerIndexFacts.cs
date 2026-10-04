using DigitalBrain.Postgres;
using Orleans.Core;
using Orleans.Runtime;

namespace DigitalBrain.Modules.Postgres.Tests;

public sealed class PostgresOwnerIndexFacts
{
    // Adoption reads a legacy owner's table list to transfer ownership. A crash between an
    // index retire and the legacy app's final persist leaves the index Retired while the app
    // still reads Installed; the handover must still answer (transfer itself no-ops on tables
    // whose state the retire already cleared), or adoption wedges retrying forever.
    [Fact]
    public async Task ARetiredOwnerIndexStillAnswersItsLegacyTablesForAdoption()
    {
        var neuron = new PostgresTablesNeuron(new FakeState(new() { Tables = ["table-1"], Retired = true }));
        Assert.Equal(["table-1"], await neuron.ReadLegacyTables());
    }

    private sealed class FakeState(PostgresTablesState state) : IPersistentState<PostgresTablesState>
    {
        public PostgresTablesState State { get; set; } = state;
        public string Etag => "";
        public bool RecordExists => true;
        public Task ClearStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => Task.CompletedTask;
    }
}
