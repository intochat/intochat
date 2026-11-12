namespace IntoChat.OS.Modules.ClickHouse.Query;

[DigitalBrain.Contracts.NeuronAlias("clickhouse"), DigitalBrain.Contracts.DefaultNeuronType(ClickHouseNames.NeuronType)]
public interface IClickHouse
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<ClickHouseQueryResult> Query(ClickHouseQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<ClickHouseSchema> ReadSchema(ReadClickHouseSchema query, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<ClickHouseConnection> ReadConnection(CancellationToken cancellationToken = default);
}
