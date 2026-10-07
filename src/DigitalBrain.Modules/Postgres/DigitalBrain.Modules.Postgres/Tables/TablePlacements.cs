using Microsoft.Extensions.Options;

namespace DigitalBrain.Postgres.Tables;

internal interface ITablePlacements
{
    Task<TablePlacement> Resolve(TableOwner owner, CancellationToken ct);
}

internal sealed class TablePlacements(IOptions<PostgresModuleOptions> options, BrainDatabases databases) : ITablePlacements
{
    public async Task<TablePlacement> Resolve(TableOwner owner, CancellationToken ct)
        => options.Value.Isolation == PostgresIsolation.DatabasePerBrain
            ? TablePlacement.BrainDatabase(await databases.Ensure(owner.BrainId, ct))
            : TablePlacement.Shared;
}
