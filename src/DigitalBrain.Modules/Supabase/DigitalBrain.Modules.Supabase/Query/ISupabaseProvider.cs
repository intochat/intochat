using DigitalBrain.Supabase.Tables;

namespace DigitalBrain.Supabase;

// The one live-table source contract: describe a read-only query, serve a compiled page, and
// compute an aggregate over the filtered view. The Supabase provider is the only implementation
// today; a connector plugs in by implementing this contract.
public interface ILiveTableSource
{
    Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken);

    Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default);
}

// The interface between the neurons and a Supabase server.
public interface ISupabaseProvider : ILiveTableSource
{
    Task<SupabaseQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<SupabaseParameter> parameters, CancellationToken cancellationToken);

    Task<SupabaseSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken);

    Task<SupabaseConnection> PingAsync(CancellationToken cancellationToken);
}

// One page of a live table: the base query plus the ui view, compiled server-side.
// Serializable: a live table's page is served inside the brain's own connection neuron.
[GenerateSerializer, Alias("supabase.query-plan")]
public sealed record QueryPlan(
    [property: Id(0)] string BaseSql,
    [property: Id(1)] IReadOnlyList<SupabaseColumn> Columns,
    [property: Id(2)] IReadOnlyList<SupabaseTableFilter> Filters,
    [property: Id(3)] SupabaseTableSort? Sort,
    [property: Id(4)] int Offset,
    [property: Id(5)] int Limit);

[GenerateSerializer, Alias("supabase.query-page")]
public sealed record QueryPage([property: Id(0)] IReadOnlyList<SupabaseTableRow> Rows, [property: Id(1)] long Total, [property: Id(2)] long Filtered);
