using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Supabase.Tables;
using Orleans.Concurrency;

namespace DigitalBrain.Supabase;

// The live-table face of a Supabase connection neuron. The provider work runs inside the neuron's
// turn, so the unsealed connection string never leaves it. Module plumbing only: never on an edge.
[PlatformOnly, Alias("supabase.source"), Orleans.Metadata.DefaultGrainType(SupabaseNames.NeuronType)]
internal interface ISupabaseConnectionSource : INeuron
{
    [ReadOnly] Task<SupabaseQueryResult> Query(string sql, int maxRows, SupabaseParameter[] parameters, CancellationToken cancellationToken);
    [ReadOnly] Task<SupabaseColumn[]> Describe(string sql, CancellationToken cancellationToken);
    [ReadOnly] Task<SupabaseSchema> ReadSchema(string? table, CancellationToken cancellationToken);
    [ReadOnly] Task<SupabaseConnection> Ping(CancellationToken cancellationToken);
    [ReadOnly] Task<QueryPage> ExecutePlan(QueryPlan plan, CancellationToken cancellationToken);
    [ReadOnly] Task<SupabaseTableAggregate> Aggregate(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken);
}

// What live tables and their agent tools read: the calling brain's own supabase/supabase connection,
// resolved per call. A brain without one is refused; nothing falls back to the deployment's store.
internal sealed class AmbientSupabaseSource(IGrainFactory grains) : ISupabaseProvider
{
    private const string ConnectionId = "supabase";

    private ISupabaseConnectionSource Source
        => grains.GetConnectionGrain<ISupabaseConnectionSource>(ConnectionKeys.For(BrainScope.CurrentId(), "supabase", ConnectionId));

    public Task<SupabaseQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<SupabaseParameter> parameters, CancellationToken cancellationToken)
        => Source.Query(sql, maxRows, [.. parameters], cancellationToken);
    public async Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken)
        => await Source.Describe(sql, cancellationToken);
    public Task<SupabaseSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
        => Source.ReadSchema(table, cancellationToken);
    public Task<SupabaseConnection> PingAsync(CancellationToken cancellationToken)
        => Source.Ping(cancellationToken);
    public Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken)
        => Source.ExecutePlan(Portable(plan), cancellationToken);
    public Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default)
        => Source.Aggregate(Portable(plan), function, columnId, cancellationToken);

    // The plan crosses a grain call; arrays, not whatever list type the caller built, cross it.
    internal static QueryPlan Portable(QueryPlan plan) => plan with { Columns = plan.Columns.ToArray(), Filters = plan.Filters.ToArray() };
}
