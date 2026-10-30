using System.Globalization;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Postgres;
using AppTable = IntoChat.OS.Modules.Postgres.Tables.ITable;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

// The sole writer of query results: a refresh request names a view (or all views) and this behavior
// reads the current source and filters, retries when it was superseded, and publishes the declarative view.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var connection = brain.Setting("Account__db") ?? "";
var json = Views.Json;
await foreach (var request in brain.On(app, "refresh", brain.Stopping))
{
    foreach (var view in (await Views.All(brain, appKey)).Where(view => request.Input.Length == 0 || view.Id == request.Input))
    { while (!await Run(await Views.Current(brain, appKey, view.Id))) { brain.Stopping.ThrowIfCancellationRequested(); } }
}

async Task<bool> Run(DataViewDefinition? view)
{
    if (view is null) { return true; }
    var root = $"{appKey}/views/{view.Id}";
    var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
    var publisher = new ViewPublisher(brain.Get<IView>($"{root}/view"), revision);
    try
    {
        if (view.Source == "sql" && connection.Length == 0) { throw new InvalidOperationException("Connect a Postgres database to this app."); }
        var values = new List<string>();
        foreach (var column in view.Filters)
        {
            values.Add(await Views.ReadFilterValue(brain, $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
        }
        var (columns, rows) = view.Source == "sql" ? await RunSql(view, values) : await RunAppTable(view, values);
        var latest = await Views.Current(brain, appKey, view.Id);
        if (latest is null || JsonSerializer.Serialize(latest, json) != JsonSerializer.Serialize(view, json)) { return false; }
        for (var index = 0; index < view.Filters.Length; index++)
        {
            if (await Views.ReadFilterValue(brain, $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{view.Filters[index]}") != values[index]) { return false; }
        }
        brain.Stopping.ThrowIfCancellationRequested();
        var summary = $"{rows.Length} rows shown (maximum {PostgresQuery.DefaultMaxRows}) · {view.Title} · {DateTimeOffset.UtcNow:HH:mm:ss} UTC";
        await publisher.Publish(ViewUi.Column(
            view.Id,
            [
                ViewUi.Text("title", view.Title),
                ViewUi.Text("columns", string.Join(", ", columns.Select(Label))),
                ViewUi.Text("status", summary),
                .. rows.Select((row, idx) => ViewUi.Text($"row-{idx}", string.Join(" | ", row))),
            ]));
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        await publisher.Publish(ViewUi.Column(
            view.Id,
            [
                ViewUi.Text("title", view.Title),
                ViewUi.Text("status", "Query failed: " + error.Message),
            ]));
    }
    return true;
}

async Task<(string[] Columns, string[][] Rows)> RunSql(DataViewDefinition view, List<string> values)
{
    var parameters = view.Filters.Select((column, index) => new PostgresParameter(column, values[index])).ToArray();
    var sql = view.Sql.Trim().TrimEnd(';');
    if (view.Filters.Length > 0)
    {
        var predicates = view.Filters.Select((column, index) => $"(${index + 1}::text = '' or data.\"{column.Replace("\"", "\"\"")}\"::text = ${index + 1}::text)");
        sql = $"select * from ({sql}) as data where {string.Join(" and ", predicates)}";
    }
    var postgres = brain.Get<IPostgresServer>($"{brain.Setting("Brain")}/connection/postgres/{connection}");
    var result = await postgres.Query(new(sql, PostgresQuery.DefaultMaxRows, parameters));
    return (result.Columns.Select(c => c.Name).ToArray(), result.Rows.Select(row => row.Select(Cell).ToArray()).ToArray());
}

async Task<(string[] Columns, string[][] Rows)> RunAppTable(DataViewDefinition view, List<string> values)
{
    var table = brain.Get<AppTable>(view.Source["apptable:".Length..]);
    var matched = new List<string[]>();
    string[] columns = [];
    for (var offset = 0; matched.Count < PostgresQuery.DefaultMaxRows; offset += PostgresQuery.DefaultMaxRows)
    {
        brain.Stopping.ThrowIfCancellationRequested();
        var page = await table.Page(offset, PostgresQuery.DefaultMaxRows);
        if (page.Length == 0) { break; }
        columns = page[0].Select(cell => cell.Column).ToArray();
        foreach (var sourceRow in page)
        {
            var row = sourceRow.Select(cell => Cell(cell.Json)).ToArray();
            var matches = view.Filters.Select((column, index) => (column, index)).All(filter =>
            {
                if (values[filter.index].Length == 0) { return true; }
                var position = Array.IndexOf(columns, filter.column);
                if (position < 0) { throw new InvalidOperationException("Unknown filter column " + filter.column); }
                return row[position] == values[filter.index];
            });
            if (matches) { matched.Add(row); }
            if (matched.Count == PostgresQuery.DefaultMaxRows) { break; }
        }
        if (page.Length < PostgresQuery.DefaultMaxRows) { break; }
    }
    return (columns, matched.ToArray());
}

static string Label(string column) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(column.Replace('_', ' '));

static string Cell(string jsonLiteral)
{
    try
    {
        using var document = JsonDocument.Parse(jsonLiteral);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => document.RootElement.GetRawText(),
        };
    }
    catch (JsonException) { return jsonLiteral; }
}
