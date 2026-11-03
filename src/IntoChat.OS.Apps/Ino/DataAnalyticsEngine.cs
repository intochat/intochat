using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

public sealed record DataQueryResult(
    string ChartTitle,
    string[] Columns,
    string[][] Rows,
    string ChartKind,
    InoChartPoint[] ChartPoints,
    string Summary,
    string SourceUri,
    double TotalValue,
    double AverageValue,
    double MinValue,
    double MaxValue);

public static partial class DataAnalyticsEngine
{
    private sealed record DatasetRecord(string[] Columns, List<string[]> Rows, string Title, string CurrencyPrefix, string EntityLabel);

    private static readonly DatasetRecord BuiltInOpportunities = new(
        ["Opportunity", "Account", "Stage", "Amount"],
        [
            ["Havbris pilot", "Havbris AS", "Proposal", "€240,000"],
            ["Depot software, phase 2", "Acme Logistics", "Negotiation", "€186,000"],
            ["Fjordline renewal", "Fjordline", "Contract", "€96,000"],
            ["Nordkapp second region", "Nordkapp Shipping", "Discovery", "€74,500"],
            ["Cold-chain sensors", "Lofoten Seafood", "Proposal", "€31,200"],
        ],
        "Pipeline Opportunities by Amount",
        "€",
        "opportunity row(s)");

    private static readonly DatasetRecord BuiltInCustomers = new(
        ["Account", "City", "Owner", "AnnualRevenue"],
        [
            ["Acme Logistics", "Oslo", "Maja Berg", "€312,000"],
            ["Havbris AS", "Bergen", "Jeff Tilley", "€240,000"],
            ["Fjordline", "Stavanger", "Erik Nilsen", "€192,000"],
            ["Nordkapp Shipping", "Tromsø", "Maja Berg", "€148,500"],
        ],
        "Customer Accounts by Annual Revenue",
        "€",
        "customer account(s)");

    public static async Task<DataQueryResult> ExecuteAsync(
        string queryOrPrompt,
        string database,
        IFilesVfsService? filesVfs = null)
    {
        var chartKind = InferChartKind(queryOrPrompt);

        // 1. Check if the query references a CSV/TSV/JSON dataset in IFilesVfsService
        if (filesVfs is not null && TryExtractDatasetFileToken(queryOrPrompt) is { } fileToken)
        {
            var resolved = await filesVfs.ResolveFileAsync(fileToken);
            var text = await filesVfs.ReadTextAsync(resolved?.VfsPath ?? fileToken);
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
                        sourceLabel: vfsPath,
                        sourceUri: resolved?.NeuronUri ?? $"neuron://local/files{vfsPath}#dataset",
                        preserveExactBuiltInSummary: false);
                }
            }
        }

        // 2. Otherwise use built-in relational tables (opportunities / customers)
        var isCustomerTable = queryOrPrompt.Contains("customer", StringComparison.OrdinalIgnoreCase)
            || (queryOrPrompt.Contains("account", StringComparison.OrdinalIgnoreCase)
                && !queryOrPrompt.Contains("opportunit", StringComparison.OrdinalIgnoreCase));

        var dataset = isCustomerTable ? BuiltInCustomers : BuiltInOpportunities;
        var hasCustomFilterOrGroup = HasAnalyticalClauses(queryOrPrompt);

        return RunRelationalPipeline(
            dataset,
            queryOrPrompt,
            chartKind,
            sourceLabel: database,
            sourceUri: "neuron://local/postgres/main#query-result",
            preserveExactBuiltInSummary: !hasCustomFilterOrGroup);
    }

    public static DataQueryResult ExecuteBuiltIn(string queryOrPrompt, string database)
    {
        var chartKind = InferChartKind(queryOrPrompt);
        var isCustomerTable = queryOrPrompt.Contains("customer", StringComparison.OrdinalIgnoreCase)
            || (queryOrPrompt.Contains("account", StringComparison.OrdinalIgnoreCase)
                && !queryOrPrompt.Contains("opportunit", StringComparison.OrdinalIgnoreCase));

        var dataset = isCustomerTable ? BuiltInCustomers : BuiltInOpportunities;
        var hasCustomFilterOrGroup = HasAnalyticalClauses(queryOrPrompt);

        return RunRelationalPipeline(
            dataset,
            queryOrPrompt,
            chartKind,
            sourceLabel: database,
            sourceUri: "neuron://local/postgres/main#query-result",
            preserveExactBuiltInSummary: !hasCustomFilterOrGroup);
    }

    public static string? TryExtractDatasetFileToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DatasetFileRegex().Match(text);
        return match.Success ? match.Groups["file"].Value.Trim('"', '\'', '`', ',', ';', ')') : null;
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
        var fileName = Path.GetFileName(vfsPath);
        var title = $"{Path.GetFileNameWithoutExtension(fileName)} Analytics";
        var trimmed = rawText.Trim();

        if (vfsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('['))
        {
            return ParseJsonDataset(title, trimmed);
        }

        var separator = vfsPath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('\t', StringComparison.Ordinal)
            ? '\t'
            : ',';
        var lines = trimmed
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        var currency = DetectCurrencyPrefix(rows);
        return new DatasetRecord(headers, rows, title, currency, "row(s)");
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
            var rows = new List<string[]>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in item.EnumerateObject())
                {
                    if (!columns.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        columns.Add(prop.Name);
                    }
                }
            }

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var row = new string[columns.Count];
                for (var c = 0; c < columns.Count; c++)
                {
                    row[c] = item.TryGetProperty(columns[c], out var val)
                        ? (val.ValueKind == JsonValueKind.String ? val.GetString() ?? string.Empty : val.GetRawText())
                        : string.Empty;
                }

                rows.Add(row);
            }

            return new DatasetRecord(columns.ToArray(), rows, title, DetectCurrencyPrefix(rows), "row(s)");
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
        string sourceUri,
        bool preserveExactBuiltInSummary)
    {
        var columns = dataset.Columns;
        var workingRows = dataset.Rows.ToList();
        var numericColIndex = FindNumericColumnIndex(columns, workingRows);
        var labelColIndex = 0;

        // 1. Apply WHERE clause if present
        if (TryExtractWherePredicate(query, columns, out var whereColIdx, out var op, out var rhs))
        {
            workingRows = workingRows.Where(row => EvaluateRowCondition(row, whereColIdx, op, rhs)).ToList();
        }

        // 2. Apply GROUP BY or natural-language "group by <col>" / "by stage" aggregation
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
                    var formatted = aggFunc == "COUNT"
                        ? $"{aggVal:N0}"
                        : $"{dataset.CurrencyPrefix}{aggVal:N0}";
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
            // 3. Apply ORDER BY if present
            if (query.Contains(" ASC", StringComparison.OrdinalIgnoreCase) && numericColIndex >= 0)
            {
                workingRows = workingRows.OrderBy(r => ParseNumber(r[numericColIndex])).ToList();
            }

            // 4. Apply LIMIT if present (ignoring default LIMIT 5 when there are 5 rows)
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
        var summary = preserveExactBuiltInSummary
            ? $"Queried {finalRows.Length} {dataset.EntityLabel} totaling {formattedTotal} from {sourceLabel}."
            : $"Queried {finalRows.Length} {dataset.EntityLabel} totaling {formattedTotal} (avg {dataset.CurrencyPrefix}{avg.ToString("N0", CultureInfo.InvariantCulture)}, max {dataset.CurrencyPrefix}{max.ToString("N0", CultureInfo.InvariantCulture)}) from {sourceLabel}.";

        return new DataQueryResult(
            chartTitle,
            columns,
            finalRows,
            chartKind,
            points,
            summary,
            sourceUri,
            total,
            avg,
            min,
            max);
    }

    private static bool HasAnalyticalClauses(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (query.Contains("WHERE ", StringComparison.OrdinalIgnoreCase)
            || query.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            || query.Contains("group by", StringComparison.OrdinalIgnoreCase)
            || query.Contains("sum by", StringComparison.OrdinalIgnoreCase)
            || query.Contains("average by", StringComparison.OrdinalIgnoreCase)
            || query.Contains("AVG(", StringComparison.OrdinalIgnoreCase)
            || query.Contains("SUM(", StringComparison.OrdinalIgnoreCase)
            || query.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var limitMatch = LimitRegex().Match(query);
        if (limitMatch.Success
            && int.TryParse(limitMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n < 5)
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractWherePredicate(
        string query,
        string[] columns,
        out int colIdx,
        out string op,
        out string rhs)
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

        for (var i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i], colName, StringComparison.OrdinalIgnoreCase))
            {
                colIdx = i;
                return true;
            }
        }

        return false;
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
            for (var i = 0; i < columns.Length; i++)
            {
                if (string.Equals(columns[i], colName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        foreach (var (col, idx) in columns.Select((c, i) => (c, i)))
        {
            if (query.Contains($"by {col}", StringComparison.OrdinalIgnoreCase)
                && (query.Contains("group", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("sum", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("total", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("average", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("breakdown", StringComparison.OrdinalIgnoreCase)))
            {
                return idx;
            }
        }

        return -1;
    }

    private static string InferAggregationFunction(string query)
    {
        if (query.Contains("AVG(", StringComparison.OrdinalIgnoreCase) || query.Contains("average", StringComparison.OrdinalIgnoreCase))
        {
            return "AVG";
        }
        if (query.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) || query.Contains("count by", StringComparison.OrdinalIgnoreCase))
        {
            return "COUNT";
        }
        if (query.Contains("MIN(", StringComparison.OrdinalIgnoreCase))
        {
            return "MIN";
        }
        if (query.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
        {
            return "MAX";
        }
        return "SUM";
    }

    private static string InferChartKind(string queryOrPrompt)
    {
        if (queryOrPrompt.Contains("pie", StringComparison.OrdinalIgnoreCase))
        {
            return "pie";
        }
        if (queryOrPrompt.Contains("line", StringComparison.OrdinalIgnoreCase))
        {
            return "line";
        }
        return "bar";
    }

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
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return false;
        }

        var cleaned = CleanNumericString(cell);
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    public static double ParseNumber(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return 0;
        }

        var cleaned = CleanNumericString(cell);
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static string CleanNumericString(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch) || ch is '.' or '-')
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string DetectCurrencyPrefix(IReadOnlyList<string[]> rows)
    {
        foreach (var row in rows)
        {
            foreach (var cell in row)
            {
                if (cell.Contains('€', StringComparison.Ordinal))
                {
                    return "€";
                }
                if (cell.Contains('$', StringComparison.Ordinal))
                {
                    return "$";
                }
            }
        }
        return string.Empty;
    }

    private static string[] SplitDelimitedLine(string line, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == separator && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result.ToArray();
    }

    private static string EscapeCsvCell(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
        return value;
    }

    [GeneratedRegex(@"(?<file>(?:/work/)?[a-zA-Z0-9_\-\.]+\.(?:csv|tsv|json))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DatasetFileRegex();

    [GeneratedRegex(@"\bWHERE\s+(?<col>[a-zA-Z0-9_]+)\s*(?<op>>=|<=|!=|<>|=|>|<|LIKE)\s*(?<val>'[^']*'|""[^""]*""|[^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WhereClauseRegex();

    [GeneratedRegex(@"\bGROUP\s+BY\s+(?<col>[a-zA-Z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GroupByRegex();

    [GeneratedRegex(@"\bLIMIT\s+(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LimitRegex();
}
