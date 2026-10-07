using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Orleans.Concurrency;

namespace DigitalBrain.Postgres;

[GrainType("postgres")]
internal sealed class PostgresServerNeuron(IPostgresProvider platform, IConnectionPostgresProviderFactory connections, IModuleConnectionCredentials<PostgresModule> credentials) : Neuron, IPostgresServer
{
    [ReadOnly]
    public async Task<PostgresQueryResult> Query(PostgresQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaxRows is < 1 or > PostgresQuery.MaxRowsLimit)
        {
            throw new PostgresQueryException($"maxRows must be between 1 and {PostgresQuery.MaxRowsLimit}.");
        }
        var parameters = query.Parameters ?? [];
        if (parameters.Length > PostgresQuery.MaxParameters)
        {
            throw new PostgresQueryException($"A query takes at most {PostgresQuery.MaxParameters} parameters.");
        }
        string sql;
        try { sql = PostgresQueryGuard.Normalize(query.Sql); }
        catch (ArgumentException error) { throw new PostgresQueryException(error.Message); }
        return await (await Resolve()).QueryAsync(sql, query.MaxRows, parameters, CancellationToken.None);
    }

    [ReadOnly]
    public async Task<PostgresSchema> ReadSchema(ReadPostgresSchema query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var table = string.IsNullOrWhiteSpace(query.Table) ? null : query.Table.Trim();
        if (table is not null && (table.Length > 255 || !table.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.')))
        {
            throw new PostgresQueryException("table must be a plain table name, optionally prefixed with its schema.");
        }
        return await (await Resolve()).ReadSchemaAsync(table, CancellationToken.None);
    }

    [ReadOnly]
    public async Task<PostgresConnection> ReadConnection() => await (await Resolve()).PingAsync(CancellationToken.None);

    private async Task<IPostgresProvider> Resolve()
    {
        var key = this.GetPrimaryKeyString();
        if (!key.StartsWith("connection/", StringComparison.Ordinal)) { return platform; }
        (string BrainId, string ConnectorId, string ConnectionId) parsed;
        try { parsed = ConnectionKeys.Parse(key); }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (parsed.BrainId != BrainScope.CurrentId() || parsed.ConnectorId != "postgres")
        { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
        var connection = await credentials.ReadConnectionAsync(parsed.ConnectorId, parsed.ConnectionId);
        return connections.ForConnectionString(connection);
    }
}
