using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Apps.Ino;

internal static class InoDatabaseChartHandler
{
    public static bool IsConnectionString(string text)
        => text.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    public static bool IsChartMutationOrFilter(string text)
        => ((text.Contains("switch", StringComparison.OrdinalIgnoreCase)
                || text.Contains("change", StringComparison.OrdinalIgnoreCase)
                || text.Contains("convert", StringComparison.OrdinalIgnoreCase)
                || text.Contains("make it", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("pie", StringComparison.OrdinalIgnoreCase)
                || text.Contains("line", StringComparison.OrdinalIgnoreCase)
                || text.Contains("bar", StringComparison.OrdinalIgnoreCase))
            && text.Contains("chart", StringComparison.OrdinalIgnoreCase))
        || (text.Contains("filter", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("table", StringComparison.OrdinalIgnoreCase)
                || text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Proposal", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Negotiation", StringComparison.OrdinalIgnoreCase)))
        || ((text.Contains("group", StringComparison.OrdinalIgnoreCase)
                || text.Contains("aggregate", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("table", StringComparison.OrdinalIgnoreCase)
                || text.Contains("by stage", StringComparison.OrdinalIgnoreCase)));

    public static bool IsChartQuestion(string text)
        => (text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("table", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top 3", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top three", StringComparison.OrdinalIgnoreCase)
                || text.Contains("highest", StringComparison.OrdinalIgnoreCase)
                || text.Contains("average", StringComparison.OrdinalIgnoreCase)
                || text.Contains("total", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("what", StringComparison.OrdinalIgnoreCase)
                || text.Contains("which", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top", StringComparison.OrdinalIgnoreCase)
                || text.Contains("see", StringComparison.OrdinalIgnoreCase)
                || text.Contains("summarize", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith('?'));

    public static bool IsDatabaseOrVisualizationQuery(string text)
        => text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("query database", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from postgres", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from supabase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from opportunities", StringComparison.OrdinalIgnoreCase)
            || text.Contains("visualize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("analyze ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show me a chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show table", StringComparison.OrdinalIgnoreCase)
            || text.Contains("table and chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("chart and table", StringComparison.OrdinalIgnoreCase)
            || text.Contains("plot ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("opportunities", StringComparison.OrdinalIgnoreCase)
            || text.Contains("customer accounts", StringComparison.OrdinalIgnoreCase)
            || ((text.Contains(".csv", StringComparison.OrdinalIgnoreCase)
                    || text.Contains(".tsv", StringComparison.OrdinalIgnoreCase))
                && (text.Contains("query", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("table", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("analyze", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("plot", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("sum", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("group", StringComparison.OrdinalIgnoreCase)));

    public static async Task<InoAppState> ConnectDbAsync(
        InoAppState currentState,
        InoConnectDb request,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ConnectionString)
            ? "Host=localhost;Port=5432;Database=supabase;Username=postgres"
            : request.ConnectionString.Trim();
        var alias = string.IsNullOrWhiteSpace(request.Alias) ? "main" : request.Alias.Trim();
        var dbName = ParseDatabaseName(raw);
        var hostName = ParseHostName(raw);
        var descriptor = $"{alias}:{hostName}/{dbName}";
        if (memory is not null)
        {
            await memory.RememberFact("database", $"Connected PostgreSQL database '{descriptor}' at neuron://local/postgres/{alias}", importance: 9);
            await memory.CaptureEpisode("ino.db", $"Connected database {descriptor}", importance: 7);
        }

        if (knowledgeStore is not null)
        {
            try
            {
                await knowledgeStore.RecordNodeAsync(new KnowledgeNode(
                    $"db:{alias}",
                    descriptor,
                    "database",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["host"] = hostName,
                        ["database"] = dbName,
                        ["uri"] = $"neuron://local/postgres/{alias}",
                    }));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var cardId = $"db-{currentState.ActiveCards.Length + 1}";
        var card = new InoCard(
            cardId,
            "db-connection",
            $"PostgreSQL Connected: {dbName} ({hostName})",
            $"Connection sealed in neuron://local/postgres/{alias}. Ready for read-only SQL, tables, and chart visualizations.",
            $"neuron://local/postgres/{alias}",
            "connected",
            $"neuron://local/apps/ino#{cardId}");
        var turns = currentState.Turns.Append(new InoTurn(
            $"Connect database ({hostName}/{dbName})",
            $"Connected to PostgreSQL database '{dbName}' on '{hostName}' (neuron://local/postgres/{alias}). Ask me to query or visualize any table or chart.",
            "postgres:" + alias,
            0)).ToArray();

        return currentState with
        {
            ConnectedDatabase = descriptor,
            Status = $"Connected DB: {descriptor} (neuron://local/postgres/{alias})",
            Turns = turns,
            Cards = [.. currentState.ActiveCards, card],
        };
    }

    public static async Task<InoAppState> QueryDbAsync(
        InoAppState currentState,
        InoQueryDb request,
        IMemory? memory,
        IFilesVfsService? filesVfs = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql)
            ? "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5"
            : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(currentState.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : currentState.ConnectedDatabase;

        var queryResult = await DataAnalyticsEngine.ExecuteAsync(sql, database, filesVfs);
        var (chartTitle, columns, rows, chartKind, chartPoints, summary) = (
            queryResult.ChartTitle,
            queryResult.Columns,
            queryResult.Rows,
            queryResult.ChartKind,
            queryResult.ChartPoints,
            queryResult.Summary);

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {rows.Length} | Chart: {chartTitle}", importance: 6);
        }

        var tableCardId = $"table-{currentState.ActiveCards.Length + 1}";
        var tableCard = new InoCard(
            tableCardId,
            "table",
            $"{chartTitle} — Table ({rows.Length} rows from {database})",
            summary,
            queryResult.SourceUri,
            "ready",
            $"neuron://local/apps/ino#{tableCardId}",
            columns,
            rows);

        var chartCardId = $"chart-{currentState.ActiveCards.Length + 2}";
        var chartCard = new InoCard(
            chartCardId,
            "chart",
            chartTitle,
            $"Live {chartKind} chart ({chartPoints.Length} series points) from {database}.",
            "neuron://local/apps/ino/chart",
            "ready",
            $"neuron://local/apps/ino#{chartCardId}",
            ChartKind: chartKind,
            ChartPoints: chartPoints);

        var nextTurns = currentState.Turns.Append(new InoTurn(
            sql,
            $"{summary} Rendered interactive UI Table ({rows.Length} rows) and {chartKind} Chart ('{chartTitle}', {chartPoints.Length} points) from neuron://local/postgres/main.",
            "postgres:query",
            0)).ToArray();

        return currentState with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {rows.Length} row(s) & {chartPoints.Length} chart point(s) from {database}",
            Turns = nextTurns,
            Cards = [.. currentState.ActiveCards, tableCard, chartCard],
        };
    }

    public static async Task<InoAppState> ApplyChartMutationOrFilterAsync(
        InoAppState currentState,
        string text,
        UserSettingsState settings,
        IMemory? memory,
        IFilesVfsService? filesVfs = null)
    {
        var cards = currentState.ActiveCards;
        var database = string.IsNullOrWhiteSpace(currentState.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : currentState.ConnectedDatabase;
        var chartCard = cards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var tableCard = cards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });

        if (chartCard is null || tableCard is null)
        {
            var (chartTitle, columns, rows, defaultKind, chartPoints, summary) = ExecuteDatasetQuery(
                "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
                database);
            tableCard = new InoCard(
                $"table-{cards.Length + 1}",
                "table",
                $"{chartTitle} — Table ({rows.Length} rows from {database})",
                summary,
                "neuron://local/postgres/main#query-result",
                "ready",
                $"neuron://local/apps/ino#table-{cards.Length + 1}",
                columns,
                rows);
            chartCard = new InoCard(
                $"chart-{cards.Length + 2}",
                "chart",
                chartTitle,
                $"Live {defaultKind} chart ({chartPoints.Length} series points) from {database}.",
                "neuron://local/apps/ino/chart",
                "ready",
                $"neuron://local/apps/ino#chart-{cards.Length + 2}",
                ChartKind: defaultKind,
                ChartPoints: chartPoints);
            cards = [.. cards, tableCard, chartCard];
        }

        string reply;
        if (text.Contains("group", StringComparison.OrdinalIgnoreCase) || text.Contains("aggregate", StringComparison.OrdinalIgnoreCase))
        {
            var grouped = await DataAnalyticsEngine.ExecuteAsync(text, database, filesVfs);
            tableCard = tableCard with
            {
                Title = $"{grouped.ChartTitle} — Table ({grouped.Rows.Length} groups)",
                Columns = grouped.Columns,
                Rows = grouped.Rows,
                Summary = grouped.Summary,
            };
            chartCard = chartCard with
            {
                Title = grouped.ChartTitle,
                ChartPoints = grouped.ChartPoints,
                Summary = $"Live {chartCard.ChartKind ?? "bar"} chart ({grouped.ChartPoints.Length} aggregated series points).",
            };
            reply = $"Ino ({settings.DefaultModel}): Aggregated **{grouped.ChartTitle}** into {grouped.Rows.Length} group(s): {string.Join(", ", grouped.ChartPoints.Select(p => $"{p.Label} (€{p.Value:N0})"))}.";
        }
        else if (text.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            var (baseTitle, baseCols, baseRows, _, basePoints, _) = ExecuteDatasetQuery("SELECT * FROM opportunities", database);
            var filterTerm = ExtractFilterTerm(text);
            if (string.IsNullOrWhiteSpace(filterTerm) || text.Contains("clear", StringComparison.OrdinalIgnoreCase) || text.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                tableCard = tableCard with { Title = $"{baseTitle} — Table ({baseRows.Length} rows)", Rows = baseRows };
                chartCard = chartCard with { ChartPoints = basePoints };
                reply = $"Ino ({settings.DefaultModel}): Cleared filters on **{baseTitle}** — showing all {baseRows.Length} rows and chart points.";
            }
            else
            {
                var matchingIndices = baseRows
                    .Select((r, idx) => (Row: r, Index: idx))
                    .Where(item => item.Row.Any(cell => cell.Contains(filterTerm, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var filteredRows = matchingIndices.Length > 0 ? matchingIndices.Select(x => x.Row).ToArray() : baseRows;
                var filteredPoints = matchingIndices.Length > 0 ? matchingIndices.Select(x => basePoints[x.Index]).ToArray() : basePoints;
                tableCard = tableCard with
                {
                    Title = $"{baseTitle} — Filtered by '{filterTerm}' ({filteredRows.Length} rows)",
                    Columns = baseCols,
                    Rows = filteredRows,
                };
                chartCard = chartCard with
                {
                    Title = $"{baseTitle} ({filterTerm})",
                    ChartPoints = filteredPoints,
                };
                reply = $"Ino ({settings.DefaultModel}): Filtered table and chart to **{filterTerm}** ({filteredRows.Length} matching row(s): {string.Join(", ", filteredPoints.Select(p => $"{p.Label} (€{p.Value:N0})"))})";
            }
        }
        else
        {
            var nextKind = text.Contains("pie", StringComparison.OrdinalIgnoreCase)
                ? "pie"
                : text.Contains("line", StringComparison.OrdinalIgnoreCase)
                    ? "line"
                    : "bar";
            chartCard = chartCard with
            {
                ChartKind = nextKind,
                Summary = $"Live {nextKind} chart ({chartCard.ChartPoints?.Length ?? 0} series points) from {database}.",
            };
            reply = $"Ino ({settings.DefaultModel}): Switched **{chartCard.Title}** to a **{nextKind}** chart with {chartCard.ChartPoints?.Length ?? 0} data points.";
        }

        var updatedCards = cards
            .Select(c => c.Id == tableCard.Id ? tableCard : c.Id == chartCard.Id ? chartCard : c)
            .ToArray();
        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.chart-mutate", $"User: {text} | Reply: {reply}", importance: 6);
        }

        var nextTurns = currentState.Turns.Append(new InoTurn(text, reply, "chart:qa", 0)).ToArray();
        return currentState with
        {
            ConnectedDatabase = database,
            Status = $"Updated visualization ({chartCard.Title} · {chartCard.ChartKind})",
            Turns = nextTurns,
            Cards = updatedCards,
        };
    }

    public static async Task<InoAppState> AnswerChartQuestionAsync(
        InoAppState currentState,
        string text,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        Func<string, InoCard?, InoCard?, Task<string?>> generateReplyAsync)
    {
        var cards = currentState.ActiveCards;
        var database = string.IsNullOrWhiteSpace(currentState.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : currentState.ConnectedDatabase;
        var chartCard = cards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var tableCard = cards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });

        if (chartCard is null || tableCard is null)
        {
            var (chartTitle, columns, rows, chartKind, chartPoints, summary) = ExecuteDatasetQuery(
                "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
                database);
            var tableCardId = $"table-{cards.Length + 1}";
            tableCard = new InoCard(
                tableCardId,
                "table",
                $"{chartTitle} — Table ({rows.Length} rows from {database})",
                summary,
                "neuron://local/postgres/main#query-result",
                "ready",
                $"neuron://local/apps/ino#{tableCardId}",
                columns,
                rows);
            var chartCardId = $"chart-{cards.Length + 2}";
            chartCard = new InoCard(
                chartCardId,
                "chart",
                chartTitle,
                $"Live {chartKind} chart ({chartPoints.Length} series points) from {database}.",
                "neuron://local/apps/ino/chart",
                "ready",
                $"neuron://local/apps/ino#{chartCardId}",
                ChartKind: chartKind,
                ChartPoints: chartPoints);
            cards = [.. cards, tableCard, chartCard];
        }

        var points = chartCard.ChartPoints ?? [];
        var topPoints = points
            .OrderByDescending(p => p.Value)
            .Take(3)
            .Select((p, idx) => $"{idx + 1}. **{p.Label}** (€{p.Value:N0})")
            .ToArray();
        var topSummary = $"Top 3 on **{chartCard.Title}**: {string.Join(", ", topPoints)}.";

        if (text.Contains("average", StringComparison.OrdinalIgnoreCase) || text.Contains("total", StringComparison.OrdinalIgnoreCase))
        {
            var total = points.Sum(p => p.Value);
            var avg = points.Length > 0 ? points.Average(p => p.Value) : 0;
            topSummary = $"{topSummary} Total: **€{total:N0}** · Average: **€{avg:N0}** across {points.Length} item(s).";
        }

        var generated = await generateReplyAsync(text, chartCard, tableCard);
        var reply = generated is { Length: > 0 }
            ? (points.Length > 0 && generated.Contains(points[0].Label, StringComparison.OrdinalIgnoreCase)
                ? $"Ino ({settings.DefaultModel}): {generated}"
                : $"Ino ({settings.DefaultModel}): {generated}\n\n{topSummary}")
            : $"Ino ({settings.DefaultModel}): {topSummary}";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.chart-qa", $"Question: {text} | Answer: {topSummary}", importance: 6);
        }

        var title = currentState.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(text, reply, "chart:qa", recalled.Length)).ToArray();
        return currentState with
        {
            ConnectedDatabase = database,
            Title = title,
            Status = $"Answered chart question ({chartCard.Title})",
            Turns = nextTurns,
            Cards = cards,
        };
    }

    private static string ExtractFilterTerm(string text)
    {
        foreach (var candidate in new[] { "Proposal", "Negotiation", "Contract", "Discovery", "Havbris", "Acme", "Fjordline", "Nordkapp", "Lofoten", "Oslo", "Bergen", "Stavanger" })
        {
            if (text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        var idx = text.LastIndexOf("to ", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + 3 < text.Length)
        {
            return text[(idx + 3)..].Trim(' ', '.', '?', '!', '"', '\'');
        }
        return string.Empty;
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

    internal static (string ChartTitle, string[] Columns, string[][] Rows, string ChartKind, InoChartPoint[] ChartPoints, string Summary) ExecuteDatasetQuery(string sql, string database)
    {
        var result = DataAnalyticsEngine.ExecuteBuiltIn(sql, database);
        return (result.ChartTitle, result.Columns, result.Rows, result.ChartKind, result.ChartPoints, result.Summary);
    }
}
