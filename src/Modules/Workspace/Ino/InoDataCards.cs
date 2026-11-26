namespace IntoChat.OS.Modules.Apps;

// The one place a query result becomes Ino's table and chart cards, shared by the data tool and InoApp's query-db op.
public static class InoDataCards
{
    public const string DefaultSql = "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5";

    public const string DefaultDatabase = "supabase:supabase/postgres";

    public static (InoCard Table, InoCard Chart) Build(DataQueryResult result, string database, string tableId, string chartId, DigitalBrain.BrainId brain = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var scope = ResolveScope(brain);
        var inoKey = scope is not null
            ? IntoChat.OS.Host.Primitives.AppRef.Of(scope.Value, "ino").Key
            : "apps/ino";
        var uriPrefix = $"neuron://{inoKey}";
        var table = new InoCard(
            tableId,
            "table",
            $"{result.ChartTitle} — Table ({result.Rows.Length} rows from {database})",
            result.Summary,
            result.SourceUri,
            "ready",
            $"{uriPrefix}#{tableId}",
            result.Columns,
            result.Rows);
        var chart = new InoCard(
            chartId,
            "chart",
            result.ChartTitle,
            $"Live {result.ChartKind} chart ({result.ChartPoints.Length} series points) from {database}.",
            $"{uriPrefix}/chart",
            "ready",
            $"{uriPrefix}#{chartId}",
            ChartKind: result.ChartKind,
            ChartPoints: result.ChartPoints);
        return (table, chart);
    }

    private static DigitalBrain.BrainId? ResolveScope(DigitalBrain.BrainId brain)
    {
        if (!string.IsNullOrEmpty(brain.Value))
        {
            return brain;
        }

        return null;
    }
}
