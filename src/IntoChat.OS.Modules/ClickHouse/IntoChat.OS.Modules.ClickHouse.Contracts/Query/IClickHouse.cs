using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.ClickHouse.Query;

[Alias("clickhouse"), Orleans.Metadata.DefaultGrainType(ClickHouseNames.NeuronType)]
public interface IClickHouse : INeuron
{
    [ReadOnly]
    Task<ClickHouseQueryResult> Query(ClickHouseQuery query, CancellationToken cancellationToken = default);

    [ReadOnly]
    Task<ClickHouseSchema> ReadSchema(ReadClickHouseSchema query, CancellationToken cancellationToken = default);

    [ReadOnly]
    Task<ClickHouseConnection> ReadConnection(CancellationToken cancellationToken = default);
}
