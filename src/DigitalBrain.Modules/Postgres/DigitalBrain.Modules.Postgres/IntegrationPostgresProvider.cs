namespace DigitalBrain.Postgres;

internal sealed class IntegrationPostgresProvider(IPostgresSourceRegistry sources) : IPostgresProvider
{
    private async Task<PostgresProvider> Create(CancellationToken ct)
        => new(await sources.Get(PostgresCapacityKind.PlatformOrigin, ct));
    public async Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, CancellationToken cancellationToken)
        => await (await Create(cancellationToken)).QueryAsync(sql, maxRows, cancellationToken);
    public async Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
        => await (await Create(cancellationToken)).ReadSchemaAsync(table, cancellationToken);
    public async Task<PostgresConnection> PingAsync(CancellationToken cancellationToken)
        => await (await Create(cancellationToken)).PingAsync(cancellationToken);
}
