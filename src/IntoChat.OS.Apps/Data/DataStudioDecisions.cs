using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Data;

internal static class DataStudioDecisions
{
    public const string DefaultOwner = "app.data";

    public static DataStudioAppState DecideQueryResult(
        DataStudioAppState current,
        string source,
        string queryText,
        string preferredChartKind,
        DataQueryResult result,
        string[] datasets,
        string brainId)
    {
        var sourceUri = !string.IsNullOrWhiteSpace(result.SourceUri)
            ? result.SourceUri
            : $"neuron://{brainId}/apps/data";

        return current with
        {
            ActiveSource = source,
            ActiveQuery = queryText,
            ChartTitle = result.ChartTitle,
            ChartKind = string.IsNullOrWhiteSpace(preferredChartKind) ? result.ChartKind : preferredChartKind,
            Columns = result.Columns,
            Rows = result.Rows,
            ChartPoints = result.ChartPoints,
            Total = Math.Round(result.TotalValue, 2),
            Average = Math.Round(result.AverageValue, 2),
            Min = Math.Round(result.MinValue, 2),
            Max = Math.Round(result.MaxValue, 2),
            Summary = result.Summary,
            SourceNeuronUri = sourceUri,
            WorkspaceDatasets = datasets,
        };
    }

    public static DataStudioAppState DecideSwitchChart(DataStudioAppState current, string? kind)
    {
        var normalizedKind = (kind ?? "bar").Trim().ToLowerInvariant() switch
        {
            "pie" => "pie",
            "line" => "line",
            _ => "bar",
        };

        return current with
        {
            ChartKind = normalizedKind,
            Summary = $"Switched '{current.ChartTitle}' chart visualization to {normalizedKind.ToUpperInvariant()} ({current.Rows.Length} row(s), total €{current.Total:N0}).",
        };
    }

    public static DataStudioAppState DecideExportCsv(DataStudioAppState current, string targetPath, string[] datasets)
    {
        return current with
        {
            LastExportPath = targetPath,
            WorkspaceDatasets = datasets,
            Summary = $"Exported {current.Rows.Length} row(s) from '{current.ChartTitle}' to {targetPath}.",
        };
    }

    public static DataStudioAppState DecideToMarkdraw(DataStudioAppState current, string docPath)
    {
        return current with
        {
            LastMarkdrawPath = docPath,
            Summary = $"Generated Markdraw data-lineage diagram for '{current.ChartTitle}' at {docPath}.",
        };
    }

    public static string NormalizeSource(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)) { return trimmed.ToLowerInvariant(); }
        if (string.Equals(trimmed, "customers", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "opportunities", StringComparison.OrdinalIgnoreCase))
        {
            return $"postgres:{trimmed.ToLowerInvariant()}";
        }
        return NormalizeCsvPath(trimmed, "/work/q4-opportunities.csv");
    }

    public static string NormalizeCsvExportPath(string raw) => NormalizeCsvPath(raw, "/work/analytics-export.csv");

    public static string NormalizeCsvPath(string raw, string fallback)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(raw);
        if (string.IsNullOrWhiteSpace(clean)) { return fallback; }
        if (!Path.HasExtension(clean)) { clean += ".csv"; }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    public static string BuildDefaultQueryForSource(string source) => source.ToLowerInvariant() switch
    {
        "postgres:customers" => "SELECT account, city, owner, arr FROM customers ORDER BY arr DESC",
        "postgres:opportunities" => "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
        _ => $"SELECT * FROM {source}",
    };
}
