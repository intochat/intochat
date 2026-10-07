using DigitalBrain.Supabase.Tables;
using Npgsql;

namespace DigitalBrain.Supabase;

// Opens a user's database from the connection string a connection neuron unsealed for one call.
internal interface ISupabaseSourceFactory
{
    ISupabaseProvider ForConnectionString(string connectionString);
}

internal sealed class NpgsqlSupabaseSourceFactory : ISupabaseSourceFactory
{
    public ISupabaseProvider ForConnectionString(string connectionString) => new ConnectionStringSupabaseProvider(connectionString);

    // One data source per call, disposed with it: nothing outlives the grain turn that opened the value.
    private sealed class ConnectionStringSupabaseProvider(string connectionString) : ISupabaseProvider
    {
        private async Task<T> Execute<T>(Func<SupabaseProvider, Task<T>> operation)
        {
            await using var source = NpgsqlDataSource.Create(SupabaseConnectionSettings.Parse(connectionString).ConnectionString);
            return await operation(new SupabaseProvider(source));
        }
        public Task<SupabaseQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<SupabaseParameter> parameters, CancellationToken cancellationToken)
            => Execute(provider => provider.QueryAsync(sql, maxRows, parameters, cancellationToken));
        public Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken)
            => Execute(provider => provider.DescribeAsync(sql, cancellationToken));
        public Task<SupabaseSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
            => Execute(provider => provider.ReadSchemaAsync(table, cancellationToken));
        public Task<SupabaseConnection> PingAsync(CancellationToken cancellationToken)
            => Execute(provider => provider.PingAsync(cancellationToken));
        public Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken)
            => Execute(provider => provider.ExecutePlanAsync(plan, cancellationToken));
        public Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default)
            => Execute(provider => provider.AggregateAsync(plan, function, columnId, cancellationToken));
    }
}
