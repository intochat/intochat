using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Tables;
using DigitalBrain.Supabase.Windows;

namespace DigitalBrain.Postgres;

internal sealed class PostgresIntegrationLiveSource(IPostgresSourceRegistry sources, string origin) : ILiveTableSource
{
    private async Task<ILiveTableSource> Create(CancellationToken ct)
        => LiveTableHosting.CreatePostgresSource(await sources.Get(origin, ct));
    public async Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken)
        => await (await Create(cancellationToken)).DescribeAsync(sql, cancellationToken);
    public async Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken)
        => await (await Create(cancellationToken)).ExecutePlanAsync(plan, cancellationToken);
    public async Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default)
        => await (await Create(cancellationToken)).AggregateAsync(plan, function, columnId, cancellationToken);
}
