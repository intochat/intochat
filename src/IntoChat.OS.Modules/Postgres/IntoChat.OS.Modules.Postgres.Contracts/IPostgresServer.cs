using Orleans.Concurrency;

namespace IntoChat.OS.Postgres;

[Alias("postgres"), Orleans.Metadata.DefaultGrainType("postgres")]
public interface IPostgresServer : INeuron
{
    // Server-side caps.
    [ReadOnly, Alias("query")]
    Task<PostgresQueryResult> Query(PostgresQuery query);

    // Reads tables and columns of the configured database; omit Table for the index.
    [ReadOnly, Alias("schema")]
    Task<PostgresSchema> ReadSchema(ReadPostgresSchema query);

    [ReadOnly, Alias("connection")]
    Task<PostgresConnection> ReadConnection();
}
