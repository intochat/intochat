using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Supabase.Tables;
using Orleans.Concurrency;

namespace DigitalBrain.Supabase;

[GenerateSerializer, Alias("supabase.connection-state")]
internal sealed class SupabaseConnectionState : ConnectionState { }

// Read-only door to a database. The integration key ("default") serves the deployment's backing
// store and holds no state; a connection key serves the user's project from the connection string
// sealed in this neuron's own state. Every query, schema read and ping is served live.
[GrainType(SupabaseNames.NeuronType)]
internal sealed class SupabaseNeuron(
    ISupabaseBackingStore platform, ISupabaseSourceFactory sources,
    [PersistentState("supabase", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<SupabaseConnectionState> store)
    : ConnectionNeuron<SupabaseConnectionState>(store), ISupabase, ISupabaseConnectionSource
{
    private const int MaxTableNameLength = 255;

    // Live tables read only a user connection, never the deployment store behind "default".
    Task<SupabaseQueryResult> ISupabaseConnectionSource.Query(string sql, int maxRows, SupabaseParameter[] parameters, CancellationToken cancellationToken)
        => WithConnection(provider => provider.QueryAsync(sql, maxRows, parameters, cancellationToken));
    Task<SupabaseColumn[]> ISupabaseConnectionSource.Describe(string sql, CancellationToken cancellationToken)
        => WithConnection(async provider => (await provider.DescribeAsync(sql, cancellationToken)).ToArray());
    Task<SupabaseSchema> ISupabaseConnectionSource.ReadSchema(string? table, CancellationToken cancellationToken)
        => WithConnection(provider => provider.ReadSchemaAsync(table, cancellationToken));
    Task<SupabaseConnection> ISupabaseConnectionSource.Ping(CancellationToken cancellationToken)
        => WithConnection(provider => provider.PingAsync(cancellationToken));
    Task<QueryPage> ISupabaseConnectionSource.ExecutePlan(QueryPlan plan, CancellationToken cancellationToken)
        => WithConnection(async provider =>
        {
            // The page crosses the grain call; an array crosses it whatever list the provider built.
            var page = await provider.ExecutePlanAsync(plan, cancellationToken);
            return page with { Rows = page.Rows.ToArray() };
        });
    Task<SupabaseTableAggregate> ISupabaseConnectionSource.Aggregate(QueryPlan plan, string function, string columnId, CancellationToken cancellationToken)
        => WithConnection(provider => provider.AggregateAsync(plan, function, columnId, cancellationToken));

    private Task<T> WithConnection<T>(Func<ISupabaseProvider, Task<T>> operation)
        => ConnectionKeys.Is(this.GetPrimaryKeyString())
            ? WithProvider(operation)
            : throw new UnauthorizedAccessException("Live tables read a user connection only.");

    [ReadOnly]
    public Task<SupabaseQueryResult> Query(SupabaseQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Sql))
        {
            throw new SupabaseQueryException("sql must not be blank. " + SupabaseQueryGuard.Reason);
        }

        if (query.MaxRows is < 1 or > SupabaseQuery.MaxRowsLimit)
        {
            throw new SupabaseQueryException($"maxRows must be between 1 and {SupabaseQuery.MaxRowsLimit}.");
        }

        var parameters = query.Parameters ?? [];
        if (parameters.Length > SupabaseQuery.MaxParameters)
        {
            throw new SupabaseQueryException($"A query takes at most {SupabaseQuery.MaxParameters} parameters.");
        }

        string sql;
        try
        {
            sql = SupabaseQueryGuard.Normalize(query.Sql);
        }
        catch (ArgumentException error)
        {
            throw new SupabaseQueryException(error.Message);
        }

        return WithProvider(provider => provider.QueryAsync(sql, query.MaxRows, parameters, CancellationToken.None));
    }

    [ReadOnly]
    public Task<SupabaseSchema> ReadSchema(ReadSupabaseSchema query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var table = string.IsNullOrWhiteSpace(query.Table) ? null : query.Table.Trim();
        if (table is not null && (table.Length > MaxTableNameLength || !table.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.')))
        {
            throw new SupabaseQueryException("table must be a plain table name, optionally prefixed with public.");
        }

        return WithProvider(provider => provider.ReadSchemaAsync(table, CancellationToken.None));
    }

    [ReadOnly]
    public Task<SupabaseConnection> ReadConnection() => WithProvider(provider => provider.PingAsync(CancellationToken.None));

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        var ping = await sources.ForConnectionString(credential.Value).PingAsync(ct);
        return ping.Connected ? ConnectionProbeResult.Connected() : ConnectionProbeResult.Failing("The database did not answer.");
    }

    private Task<T> WithProvider<T>(Func<ISupabaseProvider, Task<T>> operation)
    {
        if (!ConnectionKeys.Is(this.GetPrimaryKeyString())) { return operation(platform); }
        (string Brain, string Connector, string Connection) key;
        try { key = Key; }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (key.Connector != "supabase" || key.Brain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
        return UseCredentialAsync((credential, _) => operation(sources.ForConnectionString(credential.Value)), CancellationToken.None);
    }
}
