namespace IntoChat.OS.Modules.Apps;

// The one place a query result becomes Ino's table and chart cards, shared by the data tool and InoApp's query-db op.
public static class InoDataCards
{
    public const string DefaultSql = "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5";

    public const string DefaultDatabase = "supabase:supabase/postgres";

    public static (InoCard Table, InoCard Chart) Build(DataQueryResult result, string database, string tableId, string chartId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var table = new InoCard(
            tableId,
            "table",
            $"{result.ChartTitle} — Table ({result.Rows.Length} rows from {database})",
            result.Summary,
            result.SourceUri,
            "ready",
            $"neuron://local/apps/ino#{tableId}",
            result.Columns,
            result.Rows);
        var chart = new InoCard(
            chartId,
            "chart",
            result.ChartTitle,
            $"Live {result.ChartKind} chart ({result.ChartPoints.Length} series points) from {database}.",
            "neuron://local/apps/ino/chart",
            "ready",
            $"neuron://local/apps/ino#{chartId}",
            ChartKind: result.ChartKind,
            ChartPoints: result.ChartPoints);
        return (table, chart);
    }
}
