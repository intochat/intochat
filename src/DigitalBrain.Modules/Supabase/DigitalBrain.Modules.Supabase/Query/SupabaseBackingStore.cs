using System.Collections.Concurrent;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase.Tables;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DigitalBrain.Supabase;

// Deployment-owned database access, served only to SupabaseNeuron's integration key ("default").
// Live tables and user queries reach a brain's own project through its connection neuron.
internal interface ISupabaseBackingStore : ISupabaseProvider { }

internal sealed class SupabaseBackingStore(IModuleIntegrations<SupabaseModule> integrations, IOptions<SupabaseModuleOptions> options)
    : ISupabaseBackingStore, IIntegrationStartup, IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, NpgsqlDataSource> _sources = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupabaseProvider? _current;
    private long _revision = -1;

    internal async Task<SupabaseProvider> Materialize(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var handle = integrations.Get("supabase");
            var snapshot = await handle.ReadAsync(ct);
            if (snapshot.Status != IntegrationStatus.Ready) { throw new InvalidOperationException("The Supabase backing integration is unavailable."); }
            if (_current is not null && _revision == snapshot.Revision) { return _current; }
            var released = await handle.ReleaseAsync(ct);
            if (released.Snapshot.Status != IntegrationStatus.Ready) { throw new InvalidOperationException("The Supabase backing integration is unavailable."); }
            var settings = SupabaseConnectionSettings.Parse(released.Values["ConnectionString"]);
            var source = _sources.GetOrAdd(released.Snapshot.Revision, _ => NpgsqlDataSource.Create(settings.ConnectionString));
            _current = new SupabaseProvider(source);
            _revision = released.Snapshot.Revision;
            return _current;
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (options.Value.UseBackingStore) { _ = await Materialize(cancellationToken); }
    }
    public async Task<SupabaseQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<SupabaseParameter> parameters, CancellationToken cancellationToken)
        => await (await Materialize(cancellationToken)).QueryAsync(sql, maxRows, parameters, cancellationToken);
    public async Task<IReadOnlyList<SupabaseColumn>> DescribeAsync(string sql, CancellationToken cancellationToken)
        => await (await Materialize(cancellationToken)).DescribeAsync(sql, cancellationToken);
    public async Task<SupabaseSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
        => await (await Materialize(cancellationToken)).ReadSchemaAsync(table, cancellationToken);
    public async Task<SupabaseConnection> PingAsync(CancellationToken cancellationToken)
        => await (await Materialize(cancellationToken)).PingAsync(cancellationToken);
    public async Task<QueryPage> ExecutePlanAsync(QueryPlan plan, CancellationToken cancellationToken)
        => await (await Materialize(cancellationToken)).ExecutePlanAsync(plan, cancellationToken);
    public async Task<SupabaseTableAggregate> AggregateAsync(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken = default)
        => await (await Materialize(cancellationToken)).AggregateAsync(plan, function, columnId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values) { await source.DisposeAsync(); }
    }
}
