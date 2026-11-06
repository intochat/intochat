namespace IntoChat.OS.Modules.Apps;

// The query engine reads files through a WorkspaceFilesService built once per activation, as InoFilesNeuron does.
[DigitalBrain.Neuron("ino.data")]
public sealed class InoDataNeuron : DigitalBrain.Neuron<InoDataState>, IInoData
{
    private const string DefaultDatabase = "supabase:supabase/postgres";

    private IFilesVfsService? _vfs;
    private long _revision;

    private IFilesVfsService Vfs => _vfs ??= WorkspaceFilesService.ForBrain(Context.Services, Id.Brain.Value);

    public Task<InoDataState> Read() => Task.FromResult(State);

    public async Task<InoToolResult> Connect(string connection)
    {
        var raw = string.IsNullOrWhiteSpace(connection)
            ? "Host=localhost;Port=5432;Database=supabase;Username=postgres"
            : connection.Trim();
        var dbName = ParseDatabaseName(raw);
        var hostName = ParseHostName(raw);
        var descriptor = $"main:{hostName}/{dbName}";
        try
        {
            await Save(State with { ConnectedDatabase = descriptor }, new InoDataChanged(++_revision));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not connect {dbName} on {hostName}: {error.Message}");
        }

        var cardId = $"db-{State.Charts.Length + 1}";
        var card = new InoCard(
            cardId,
            "db-connection",
            $"PostgreSQL Connected: {dbName} ({hostName})",
            "Connection sealed in neuron://local/postgres/main. Ready for read-only SQL, tables, and chart visualizations.",
            "neuron://local/postgres/main",
            "connected",
            $"neuron://local/apps/ino#{cardId}");
        return new InoToolResult($"Connected to PostgreSQL database '{dbName}' on '{hostName}'.", card);
    }

    public async Task<InoToolResult> Query(string sql)
    {
        var statement = string.IsNullOrWhiteSpace(sql)
            ? "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5"
            : sql.Trim();
        var database = State.ConnectedDatabase ?? DefaultDatabase;
        DataQueryResult result;
        try
        {
            result = await DataAnalyticsEngine.ExecuteAsync(statement, database, Vfs);
            await Remember(database, ChartCard(result, database));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not run the query: {error.Message}");
        }

        var cardId = $"table-{State.Charts.Length}";
        var table = new InoCard(
            cardId,
            "table",
            $"{result.ChartTitle} — Table ({result.Rows.Length} rows from {database})",
            result.Summary,
            result.SourceUri,
            "ready",
            $"neuron://local/apps/ino#{cardId}",
            result.Columns,
            result.Rows);
        var preview = string.Join("\n", result.Rows.Take(5).Select(row => "- " + string.Join(" | ", row)));
        return new InoToolResult($"{result.Summary}\n{result.Columns.Length} column(s), {result.Rows.Length} row(s):\n{string.Join(" | ", result.Columns)}\n{preview}", table);
    }

    public async Task<InoToolResult> Chart(string spec)
    {
        var database = State.ConnectedDatabase ?? DefaultDatabase;
        DataQueryResult result;
        try
        {
            result = await DataAnalyticsEngine.ExecuteAsync(string.IsNullOrWhiteSpace(spec) ? "chart" : spec.Trim(), database, Vfs);
            await Remember(database, ChartCard(result, database));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not build the chart: {error.Message}");
        }

        return new InoToolResult($"Rendered a {result.ChartKind} chart '{result.ChartTitle}' with {result.ChartPoints.Length} point(s).", State.Charts[^1]);
    }

    private static InoCard ChartCard(DataQueryResult result, string database)
        => new(
            "chart-pending",
            "chart",
            result.ChartTitle,
            $"Live {result.ChartKind} chart ({result.ChartPoints.Length} series points) from {database}.",
            "neuron://local/apps/ino/chart",
            "ready",
            null,
            ChartKind: result.ChartKind,
            ChartPoints: result.ChartPoints);

    // Ids carry the chart's position so cards stay distinct across calls.
    private async Task Remember(string database, InoCard chart)
    {
        var id = $"chart-{State.Charts.Length + 1}";
        var stored = chart with { Id = id, SurfaceScopeUri = $"neuron://local/apps/ino#{id}" };
        await Save(State with { ConnectedDatabase = database, Charts = [.. State.Charts, stored] }, new InoDataChanged(++_revision));
    }

    private static string ParseDatabaseName(string connectionString)
    {
        if (connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(path) ? "nordlys" : path;
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && (kv[0].Equals("Database", StringComparison.OrdinalIgnoreCase) || kv[0].Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)))
            {
                return kv[1];
            }
        }

        return "nordlys";
    }

    private static string ParseHostName(string connectionString)
    {
        if (connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && (kv[0].Equals("Host", StringComparison.OrdinalIgnoreCase) || kv[0].Equals("Server", StringComparison.OrdinalIgnoreCase)))
            {
                return kv[1];
            }
        }

        return "localhost";
    }
}
