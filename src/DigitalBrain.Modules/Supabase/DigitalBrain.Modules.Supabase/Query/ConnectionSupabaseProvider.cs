using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase.Tables;
using Npgsql;

namespace DigitalBrain.Supabase;

internal sealed class ConnectionSupabaseProvider(IModuleConnectionCredentials<SupabaseModule> credentials) : ISupabaseProvider
{
    private async Task<T> Execute<T>(Func<SupabaseProvider, Task<T>> operation, CancellationToken ct)
    {
        var connection = await credentials.ReadConnectionAsync("supabase", "supabase", ct);
        await using var source = NpgsqlDataSource.Create(SupabaseConnectionSettings.Parse(connection).ConnectionString);
        return await operation(new SupabaseProvider(source));
    }
    public Task<SupabaseQueryResult> QueryAsync(string sql, int maxRows, CancellationToken cancellationToken)
        => Execute(provider => provider.QueryAsync(sql, maxRows, cancellationToken), cancellationToken);
    public Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken)
        => Execute(provider => provider.DescribeAsync(sql, cancellationToken), cancellationToken);
    public Task<SupabaseSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
        => Execute(provider => provider.ReadSchemaAsync(table, cancellationToken), cancellationToken);
    public Task<SupabaseConnection> PingAsync(CancellationToken cancellationToken)
        => Execute(provider => provider.PingAsync(cancellationToken), cancellationToken);
    public Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken)
        => Execute(provider => provider.ExecutePlanAsync(plan, cancellationToken), cancellationToken);
    public Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default)
        => Execute(provider => provider.AggregateAsync(plan, function, columnId, cancellationToken), cancellationToken);
}
