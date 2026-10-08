using System.Globalization;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Postgres;
using AppTable = DigitalBrain.Postgres.Tables.ITable;
using ViewTable = DigitalBrain.Flutter.Table.ITable;
using ViewColumn = DigitalBrain.Flutter.Table.TableColumn;

// One writer serializes query results. Durable app invocations survive process restarts.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var connection = brain.Setting("Account__db") ?? "";
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
await foreach (var _ in brain.On<AppInvoked>(app, Reconcile, brain.Stopping)) { await Reconcile(); }

async Task Reconcile()
{
    var pending = (await app.Pending()).Where(item => item.Operation == "refresh").ToArray();
    if (pending.Length == 0) { return; }
    try
    {
        var views = await Views();
        foreach (var view in views.Where(view => pending.Any(request => request.Input.Length == 0 || view.Id == request.Input)))
        {
            while (!await Run(await Current(view.Id))) { brain.Stopping.ThrowIfCancellationRequested(); }
        }
        foreach (var request in pending) { await app.Respond(new(request.Id, "refreshed", null)); }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        foreach (var request in pending) { await app.Respond(new(request.Id, null, error.Message)); }
    }
}

async Task<bool> Run(View? view)
{
    if (view is null) { return true; }
    var root = $"{appKey}/views/{view.Id}";
    var status = brain.Get<ICard>($"{root}/status");
    try
    {
        if (view.Source == "sql" && connection.Length == 0) { throw new InvalidOperationException("Connect a Postgres database to this app."); }
        await status.Set("Loading", "Reading the current source and filters…");
        var values = new List<string>();
        foreach (var column in view.Filters) { values.Add((await brain.Get<ITextField>($"{root}/filter/{column}").Read()).Value); }
        var (columns, rows) = view.Source == "sql" ? await RunSql(view, values) : await RunAppTable(view, values);
        var latest = await Current(view.Id);
        if (latest is null || JsonSerializer.Serialize(latest, json) != JsonSerializer.Serialize(view, json)) { return false; }
        for (var index = 0; index < view.Filters.Length; index++)
        {
            if ((await brain.Get<ITextField>($"{root}/filter/{view.Filters[index]}").Read()).Value != values[index]) { return false; }
        }
        brain.Stopping.ThrowIfCancellationRequested();
        if (view.Kind == "chart")
        {
            if (columns.Length < 2) { throw new InvalidOperationException("A chart needs a label column and a numeric value column."); }
            var points = rows.Select((row, index) => new ChartPoint(index.ToString(CultureInfo.InvariantCulture), row.ElementAtOrDefault(0) ?? "",
                double.TryParse(row.ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new InvalidOperationException("The second chart column must contain finite numbers."))).ToArray();
            await brain.Get<IChart>($"{root}/chart").Render(view.Title, "bar", points);
        }
        await brain.Get<ViewTable>($"{root}/table").Replace(view.Title, columns.Select(c => new ViewColumn(c, Label(c))).ToArray(), rows);
        await status.Set(rows.Length == 0 ? "No matching rows" : "Data loaded",
            $"{rows.Length} rows shown (maximum {PostgresQuery.DefaultMaxRows}) · {view.Title} · {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        await brain.Get<ViewTable>($"{root}/table").Replace(view.Title, [], []);
        if (view.Kind == "chart") { await brain.Get<IChart>($"{root}/chart").Render(view.Title, "bar", []); }
        await status.Set("Query failed", error.Message);
    }
    return true;
}

async Task<(string[] Columns, string[][] Rows)> RunSql(View view, List<string> values)
{
    var parameters = view.Filters.Select((column, index) => new PostgresParameter(column, values[index])).ToArray();
    var sql = view.Sql.Trim().TrimEnd(';');
    if (view.Filters.Length > 0)
    {
        var predicates = view.Filters.Select((column, index) => $"(${index + 1}::text = '' or data.\"{column.Replace("\"", "\"\"")}\"::text = ${index + 1}::text)");
        sql = $"select * from ({sql}) as data where {string.Join(" and ", predicates)}";
    }
    var postgres = brain.Get<IPostgresServer>($"connection/{brain.Setting("Brain")}/postgres/{connection}");
    var result = await postgres.Query(new(sql, PostgresQuery.DefaultMaxRows, parameters));
    return (result.Columns.Select(c => c.Name).ToArray(), result.Rows.Select(row => row.Select(Cell).ToArray()).ToArray());
}

// An app table is read whole and filtered in memory: equality on each filter column, blank means any.
async Task<(string[] Columns, string[][] Rows)> RunAppTable(View view, List<string> values)
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

// Cells arrive as JSON literals; the person reads plain text.
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

async Task<View?> Current(string id) => (await Views()).FirstOrDefault(v => v.Id == id);

async Task<View[]> Views()
{
    var text = (await registry.Read()).Markdown;
    return text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, json) ?? [];
}

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds);
