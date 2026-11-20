using DigitalBrain.Capabilities;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

// The query engine reads files through a WorkspaceFilesService built once per activation, as InoFilesNeuron does.
[DigitalBrain.Neurons.Neuron("ino.data")]
internal sealed class InoDataNeuron(ICapabilityBroker? broker = null, IServiceProvider? services = null) : DigitalBrain.Neurons.Neuron<InoDataState>, IInoData
{
    private IFilesVfsService? _vfs;
    private long _revision;

    private IFilesVfsService Vfs => _vfs ??= WorkspaceFilesService.ForBrain(services, Id.Brain.Value, broker);

    public Task<InoDataState> Read() => Task.FromResult(State);

    public Task<InoToolResult> Connect(string connection)
    {
        return Task.FromResult(new InoToolResult(
            "PostgreSQL database connections are not configured in ino.data. Query CSV, TSV, or JSON files from your workspace instead.",
            Ok: false));
    }

    public async Task<InoToolResult> Query(string sql)
    {
        var statement = string.IsNullOrWhiteSpace(sql) ? InoDataCards.DefaultSql : sql.Trim();
        var database = State.ConnectedDatabase ?? "workspace-files";
        DataQueryResult result;
        InoCard table;
        try
        {
            result = await DataAnalyticsEngine.ExecuteAsync(statement, database, Vfs, Id.Brain);
            if (result.Columns.Length == 0 && result.Rows.Length == 0)
            {
                return new InoToolResult(result.Summary, Ok: false);
            }

            var index = State.Charts.Length + 1;
            var cards = InoDataCards.Build(result, database, $"table-{index}", $"chart-{index}", Id.Brain);
            table = cards.Table;
            await Save(State with { Charts = [.. State.Charts, cards.Chart] }, new InoDataChanged(++_revision));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoDataNeuron>().LogSwallowed(error, "executing Ino data query");
            return new InoToolResult($"Could not run the query: {error.Message}", Ok: false);
        }

        var preview = string.Join("\n", result.Rows.Take(5).Select(row => "- " + string.Join(" | ", row)));
        return new InoToolResult($"{result.Summary}\n{result.Columns.Length} column(s), {result.Rows.Length} row(s):\n{string.Join(" | ", result.Columns)}\n{preview}", table);
    }

    public async Task<InoToolResult> Chart(string spec)
    {
        var database = State.ConnectedDatabase ?? "workspace-files";
        DataQueryResult result;
        try
        {
            result = await DataAnalyticsEngine.ExecuteAsync(string.IsNullOrWhiteSpace(spec) ? "chart" : spec.Trim(), database, Vfs, Id.Brain);
            if (result.Columns.Length == 0 && result.ChartPoints.Length == 0)
            {
                return new InoToolResult(result.Summary, Ok: false);
            }

            var index = State.Charts.Length + 1;
            var chart = InoDataCards.Build(result, database, $"table-{index}", $"chart-{index}", Id.Brain).Chart;
            await Save(State with { Charts = [.. State.Charts, chart] }, new InoDataChanged(++_revision));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoDataNeuron>().LogSwallowed(error, "building Ino data chart");
            return new InoToolResult($"Could not build the chart: {error.Message}", Ok: false);
        }

        return new InoToolResult($"Rendered a {result.ChartKind} chart '{result.ChartTitle}' with {result.ChartPoints.Length} point(s).", State.Charts[^1]);
    }
}
