using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Modules.Apps;

public static partial class DataAnalyticsEngine
{
    private sealed record DatasetRecord(string[] Columns, List<string[]> Rows, string Title, string CurrencyPrefix, string EntityLabel);

    public static DataQueryResult ExecuteInMemory(
        string queryOrPrompt,
        string database,
        string? fileDatasetText = null,
        string? fileDatasetPath = null)
    {
        var chartKind = InferChartKind(queryOrPrompt);

        if (!string.IsNullOrWhiteSpace(fileDatasetText))
        {
            var vfsPath = fileDatasetPath ?? "/work/dataset.csv";
            var parsedDataset = ParseFileDataset(vfsPath, fileDatasetText);
            if (parsedDataset.Rows.Count > 0)
            {
                return RunRelationalPipeline(parsedDataset, queryOrPrompt, chartKind, vfsPath, $"neuron://local/files{vfsPath}#dataset");
            }
        }

        if (!queryOrPrompt.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            && TryParseInlinePromptDataset(queryOrPrompt) is { } inlineDataset)
        {
            return RunRelationalPipeline(inlineDataset, queryOrPrompt, chartKind, "inline prompt data", "neuron://local/apps/ino/chart#inline");
        }

        return CreateEmptyResult(database, chartKind);
    }

    public static async Task<DataQueryResult> ExecuteAsync(
        string queryOrPrompt,
        string database,
        IFilesVfsService? filesVfs = null)
    {
        var chartKind = InferChartKind(queryOrPrompt);

        if (filesVfs is not null && TryExtractDatasetFileToken(queryOrPrompt) is { } fileToken)
        {
            var resolved = await filesVfs.ResolveFileAsync(fileToken)
                ?? (!Path.HasExtension(fileToken) ? await filesVfs.ResolveFileAsync($"/work/{fileToken}.csv") : null)
                ?? (!Path.HasExtension(fileToken) ? await filesVfs.ResolveFileAsync($"{fileToken}.csv") : null);

            var path = resolved?.VfsPath ?? fileToken;
            var text = await filesVfs.ReadTextAsync(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var vfsPath = resolved?.VfsPath ?? $"/work/{WorkspaceFilesService.ExtractCleanFileName(fileToken)}";
                var parsedDataset = ParseFileDataset(vfsPath, text);
                if (parsedDataset.Rows.Count > 0)
                {
                    return RunRelationalPipeline(
                        parsedDataset,
                        queryOrPrompt,
                        chartKind,
                        vfsPath,
                        resolved?.NeuronUri ?? $"neuron://local/files{vfsPath}#dataset");
                }
            }
        }

        if (!queryOrPrompt.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            && TryParseInlinePromptDataset(queryOrPrompt) is { } inlineDataset)
        {
            return RunRelationalPipeline(inlineDataset, queryOrPrompt, chartKind, "inline prompt data", "neuron://local/apps/ino/chart#inline");
        }

        return CreateEmptyResult(database, chartKind);
    }

    private static DataQueryResult CreateEmptyResult(string database, string chartKind) => new(
        ChartTitle: $"No Data ({database})",
        Columns: [],
        Rows: [],
        ChartKind: chartKind,
        ChartPoints: [],
        Summary: $"No dataset found matching query or file reference in database '{database}'.",
        SourceUri: $"neuron://local/data/{database}/empty",
        TotalValue: 0.0,
        AverageValue: 0.0,
        MinValue: 0.0,
        MaxValue: 0.0);

    private static DatasetRecord? TryParseInlinePromptDataset(string prompt)
    {
        var matches = InlineKeyValueRegex().Matches(prompt);
        if (matches.Count < 2)
        {
            return null;
        }

        var rows = matches
            .Select(m => new[] { m.Groups["label"].Value.Trim(), m.Groups["val"].Value.Trim() })
            .Where(r => !string.IsNullOrWhiteSpace(r[0]))
            .ToList();

        return rows.Count >= 2
            ? new DatasetRecord(["Category", "Value"], rows, "Custom Inline Chart", DetectCurrencyPrefix(rows), "data point(s)")
            : null;
    }

    public static string? TryExtractDatasetFileToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DatasetFileRegex().Match(text);
        if (match.Success)
        {
            return match.Groups["file"].Value.Trim('"', '\'', '`', ',', ';', ')');
        }

        var fromMatch = FromTableRegex().Match(text);
        return fromMatch.Success ? fromMatch.Groups["table"].Value.Trim('"', '\'', '`', ',', ';', ')') : null;
    }

    public static string SerializeToCsv(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(EscapeCsvCell)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(EscapeCsvCell)));
        }
        return sb.ToString().TrimEnd();
    }

    private static DatasetRecord ParseFileDataset(string vfsPath, string rawText)
    {
        var title = $"{Path.GetFileNameWithoutExtension(Path.GetFileName(vfsPath))} Analytics";
        var trimmed = rawText.Trim();

        if (vfsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('['))
        {
            return ParseJsonDataset(title, trimmed);
        }

        var separator = vfsPath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('\t', StringComparison.Ordinal) ? '\t' : ',';
        var lines = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return new DatasetRecord([], [], title, string.Empty, "row(s)");
        }

        var headers = SplitDelimitedLine(lines[0], separator);
        var rows = new List<string[]>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitDelimitedLine(lines[i], separator);
            if (cells.Length == 0)
            {
                continue;
            }
            if (cells.Length < headers.Length)
            {
                Array.Resize(ref cells, headers.Length);
                for (var c = 0; c < cells.Length; c++)
                {
                    cells[c] ??= string.Empty;
                }
            }
            rows.Add(cells);
        }

        return new DatasetRecord(headers, rows, title, DetectCurrencyPrefix(rows), "row(s)");
    }

    private static DatasetRecord ParseJsonDataset(string title, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rowsProp))
            {
                root = rowsProp;
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                return new DatasetRecord([], [], title, string.Empty, "row(s)");
            }

            var columns = new List<string>();
            var objects = root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
            foreach (var prop in objects.SelectMany(item => item.EnumerateObject()))
            {
                if (!columns.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                {
                    columns.Add(prop.Name);
                }
            }

            var rows = objects.Select(item => columns.Select(col =>
                item.TryGetProperty(col, out var val)
                    ? (val.ValueKind == JsonValueKind.String ? val.GetString() ?? string.Empty : val.GetRawText())
                    : string.Empty).ToArray()).ToList();

            return new DatasetRecord([.. columns], rows, title, DetectCurrencyPrefix(rows), "row(s)");
        }
        catch (JsonException)
        {
            return new DatasetRecord([], [], title, string.Empty, "row(s)");
        }
    }

    private static DataQueryResult RunRelationalPipeline(
        DatasetRecord dataset,
        string query,
        string chartKind,
        string sourceLabel,
        string sourceUri)
    {
        var columns = dataset.Columns;
        var workingRows = dataset.Rows.ToList();
        var numericColIndex = FindNumericColumnIndex(columns, workingRows);
        var labelColIndex = 0;

        if (TryExtractWherePredicate(query, columns, out var whereColIdx, out var op, out var rhs))
        {
            workingRows = workingRows.Where(row => EvaluateRowCondition(row, whereColIdx, op, rhs)).ToList();
        }

        var groupByColIdx = TryExtractGroupByColumn(query, columns);
        var chartTitle = dataset.Title;
        if (groupByColIdx >= 0 && groupByColIdx < columns.Length && numericColIndex >= 0)
        {
            var aggFunc = InferAggregationFunction(query);
            var groupColName = columns[groupByColIdx];
            var valueColName = columns[numericColIndex];
            var grouped = workingRows
                .GroupBy(r => r[groupByColIdx], StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var nums = g.Select(r => ParseNumber(r[numericColIndex])).ToArray();
                    var aggVal = aggFunc switch
                    {
                        "AVG" => nums.Length > 0 ? nums.Average() : 0,
                        "COUNT" => nums.Length,
                        "MIN" => nums.Length > 0 ? nums.Min() : 0,
                        "MAX" => nums.Length > 0 ? nums.Max() : 0,
                        _ => nums.Sum(),
                    };
                    var formatted = aggFunc == "COUNT" ? $"{aggVal:N0}" : $"{dataset.CurrencyPrefix}{aggVal:N0}";
                    return (g.Key, Count: g.Count(), Value: aggVal, Formatted: formatted);
                })
                .OrderByDescending(g => g.Value)
                .ToList();

            columns = [groupColName, "Count", $"{aggFunc}({valueColName})"];
            workingRows = grouped.Select(g => new[] { g.Key, $"{g.Count}", g.Formatted }).ToList();
            labelColIndex = 0;
            numericColIndex = 2;
            chartTitle = $"{dataset.Title} — {aggFunc} by {groupColName}";
        }
        else
        {
            if (query.Contains(" ASC", StringComparison.OrdinalIgnoreCase) && numericColIndex >= 0)
            {
                workingRows = workingRows.OrderBy(r => ParseNumber(r[numericColIndex])).ToList();
            }

            var limitMatch = LimitRegex().Match(query);
            if (limitMatch.Success
                && int.TryParse(limitMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                && limit > 0
                && limit < workingRows.Count)
            {
                workingRows = workingRows.Take(limit).ToList();
            }
        }

        var finalRows = workingRows.ToArray();
        var points = finalRows
            .Select((r, idx) => new InoChartPoint(
                $"pt-{idx + 1}",
                r[labelColIndex],
                numericColIndex >= 0 && numericColIndex < r.Length ? ParseNumber(r[numericColIndex]) : idx + 1))
            .ToArray();

        var total = points.Sum(p => p.Value);
        var avg = points.Length > 0 ? points.Average(p => p.Value) : 0;
        var min = points.Length > 0 ? points.Min(p => p.Value) : 0;
        var max = points.Length > 0 ? points.Max(p => p.Value) : 0;

        var formattedTotal = $"{dataset.CurrencyPrefix}{total.ToString("N0", CultureInfo.InvariantCulture)}";
        var summary = $"Queried {finalRows.Length} {dataset.EntityLabel} totaling {formattedTotal} (avg {dataset.CurrencyPrefix}{avg.ToString("N0", CultureInfo.InvariantCulture)}, max {dataset.CurrencyPrefix}{max.ToString("N0", CultureInfo.InvariantCulture)}) from {sourceLabel}.";

        return new DataQueryResult(chartTitle, columns, finalRows, chartKind, points, summary, sourceUri, total, avg, min, max);
    }

    private static bool TryExtractWherePredicate(string query, string[] columns, out int colIdx, out string op, out string rhs)
    {
        colIdx = -1;
        op = "=";
        rhs = string.Empty;

        var match = WhereClauseRegex().Match(query);
        if (!match.Success)
        {
            return false;
        }

        var colName = match.Groups["col"].Value.Trim();
        op = match.Groups["op"].Value.Trim();
        rhs = match.Groups["val"].Value.Trim().Trim('\'', '"');
        colIdx = Array.FindIndex(columns, c => string.Equals(c, colName, StringComparison.OrdinalIgnoreCase));
        return colIdx >= 0;
    }

    private static bool EvaluateRowCondition(string[] row, int colIdx, string op, string rhs)
    {
        if (colIdx < 0 || colIdx >= row.Length)
        {
            return true;
        }

        var cell = row[colIdx];
        if (double.TryParse(rhs.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Any, CultureInfo.InvariantCulture, out var rhsNum))
        {
            var cellNum = ParseNumber(cell);
            return op switch
            {
                ">" => cellNum > rhsNum,
                ">=" => cellNum >= rhsNum,
                "<" => cellNum < rhsNum,
                "<=" => cellNum <= rhsNum,
                "!=" or "<>" => Math.Abs(cellNum - rhsNum) > 0.001,
                _ => Math.Abs(cellNum - rhsNum) < 0.001,
            };
        }

        return op switch
        {
            "!=" or "<>" => !string.Equals(cell, rhs, StringComparison.OrdinalIgnoreCase),
            "LIKE" or "like" => cell.Contains(rhs.Trim('%'), StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(cell, rhs, StringComparison.OrdinalIgnoreCase) || cell.Contains(rhs, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static int TryExtractGroupByColumn(string query, string[] columns)
    {
        var match = GroupByRegex().Match(query);
        if (match.Success)
        {
            var colName = match.Groups["col"].Value.Trim();
            var idx = Array.FindIndex(columns, c => string.Equals(c, colName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                return idx;
            }
        }

        if (new[] { "group", "sum", "total", "average", "breakdown" }.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (query.Contains($"by {columns[i]}", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string InferAggregationFunction(string query) => query switch
    {
        var q when q.Contains("AVG(", StringComparison.OrdinalIgnoreCase) || q.Contains("average", StringComparison.OrdinalIgnoreCase) => "AVG",
        var q when q.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) || q.Contains("count by", StringComparison.OrdinalIgnoreCase) => "COUNT",
        var q when q.Contains("MIN(", StringComparison.OrdinalIgnoreCase) => "MIN",
        var q when q.Contains("MAX(", StringComparison.OrdinalIgnoreCase) => "MAX",
        _ => "SUM",
    };

    private static string InferChartKind(string queryOrPrompt) => queryOrPrompt switch
    {
        var q when q.Contains("pie", StringComparison.OrdinalIgnoreCase) => "pie",
        var q when q.Contains("line", StringComparison.OrdinalIgnoreCase) => "line",
        _ => "bar",
    };

    private static int FindNumericColumnIndex(string[] columns, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0 || columns.Length == 0)
        {
            return -1;
        }
        for (var c = columns.Length - 1; c >= 0; c--)
        {
            if (rows.All(r => c < r.Length && HasNumericValue(r[c])))
            {
                return c;
            }
        }
        return columns.Length - 1;
    }

    private static bool HasNumericValue(string cell)
        => !string.IsNullOrWhiteSpace(cell) && double.TryParse(CleanNumericString(cell), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    public static double ParseNumber(string? cell)
        => !string.IsNullOrWhiteSpace(cell) && double.TryParse(CleanNumericString(cell), NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : 0;

    private static string CleanNumericString(string raw)
        => new(raw.Where(ch => char.IsDigit(ch) || ch is '.' or '-').ToArray());

    private static string DetectCurrencyPrefix(IReadOnlyList<string[]> rows)
    {
        foreach (var cell in rows.SelectMany(r => r))
        {
            if (cell.Contains('€', StringComparison.Ordinal)) { return "€"; }
            if (cell.Contains('$', StringComparison.Ordinal)) { return "$"; }
        }
        return string.Empty;
    }

    private static string[] SplitDelimitedLine(string line, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == separator && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString().Trim());
        return [.. result];
    }

    private static string EscapeCsvCell(string value)
        => value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    [GeneratedRegex(@"(?<file>(?:/work/)?[a-zA-Z0-9_\-\.]+\.(?:csv|tsv|json))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DatasetFileRegex();

    [GeneratedRegex(@"\b(?:FROM|OF|FOR)\s+(?<table>[a-zA-Z0-9_\-\./]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FromTableRegex();

    [GeneratedRegex(@"\bWHERE\s+(?<col>[a-zA-Z0-9_]+)\s*(?<op>>=|<=|!=|<>|=|>|<|LIKE)\s*(?<val>'[^']*'|""[^""]*""|[^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WhereClauseRegex();

    [GeneratedRegex(@"\bGROUP\s+BY\s+(?<col>[a-zA-Z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GroupByRegex();

    [GeneratedRegex(@"\bLIMIT\s+(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LimitRegex();

    [GeneratedRegex(@"(?<label>[A-Za-z][A-Za-z0-9#+\- ]{0,24}?)\s*[:=]\s*(?<val>[€$]?\d+(?:[.,]\d+)?)", RegexOptions.Compiled)]
    private static partial Regex InlineKeyValueRegex();
}
