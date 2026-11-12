
namespace IntoChat.OS.Modules.Postgres;

[DigitalBrain.Contracts.NeuronAlias("postgres"), DigitalBrain.Contracts.DefaultNeuronType("postgres")]
public interface IPostgresServer
{
    // Server-side caps.
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<PostgresQueryResult> Query(PostgresQuery query);

    // Reads tables and columns of the configured database; omit Table for the index.
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<PostgresSchema> ReadSchema(ReadPostgresSchema query);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<PostgresConnection> ReadConnection();
}
