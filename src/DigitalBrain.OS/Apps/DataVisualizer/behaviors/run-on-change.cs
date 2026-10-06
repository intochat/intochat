#:project /brain/src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Postgres/DigitalBrain.Modules.Postgres.Contracts/DigitalBrain.Modules.Postgres.Contracts.csproj
using System.Globalization;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Table;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Postgres;
using AppTable = DigitalBrain.Postgres.Tables.ITable;
using ViewTable = DigitalBrain.Flutter.Table.ITable;
using ViewColumn = DigitalBrain.Flutter.Table.TableColumn;

// When the view registry changes, every view runs again.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var connection = brain.Setting("Account__db") ?? "";
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

while (!brain.Stopping.IsCancellationRequested)
{
    try
    {
        await using var registryChanges = await brain.SubscribeAsync<TextChanged>(registry, brain.Stopping);
        await RunAll();
        await foreach (var _ in registryChanges.ReadAllAsync(brain.Stopping)) { await RunAll(); }
    }
    catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { }
    catch (Exception error) { Console.WriteLine($"registry stream stopped, resubscribing: {error.Message}"); }
    await Task.Delay(TimeSpan.FromSeconds(1), brain.Stopping).ContinueWith(_ => { });
}

// Runs on every (re)subscribe too: a restarted script must not leave views stale until the next registry write.
async Task RunAll() { foreach (var view in await Views()) { await RunView(view.Id); } }

async Task Run(View? view)
{
    if (view is null) { return; }
    var root = $"{appKey}/views/{view.Id}";
    var status = brain.Get<ICard>($"{root}/status");
    try
    {
        if (view.Source == "sql" && connection.Length == 0) { await status.Set("Error", "Connect a Postgres database to this app."); return; }
        var values = new List<string>();
        foreach (var column in view.Filters) { values.Add((await brain.Get<ITextField>($"{root}/filter/{column}").Read()).Value); }
        var (columns, rows) = view.Source == "sql" ? await RunSql(view, values) : await RunAppTable(view, values);
        if (view.Kind == "chart")
        {
            var points = rows.Select((row, index) => new ChartPoint(index.ToString(CultureInfo.InvariantCulture), row.ElementAtOrDefault(0) ?? "",
                double.TryParse(row.ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0)).ToArray();
            await brain.Get<IChart>($"{root}/chart").Render(view.Title, "bar", points);
        }
        else
        {
            await brain.Get<ViewTable>($"{root}/table").Replace(view.Title, columns.Select(c => new ViewColumn(c, c)).ToArray(), rows);
        }
        await status.Set("", $"ok · {rows.Length} rows · {DateTimeOffset.UtcNow:HH:mm:ss}");
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        await status.Set("Error", error.Message);
    }
}

async Task<(string[] Columns, string[][] Rows)> RunSql(View view, List<string> values)
{
    var parameters = view.Filters.Select((column, index) => new PostgresParameter(column, values[index])).ToArray();
    var postgres = brain.Get<IPostgresServer>($"connection/{brain.Setting("Brain")}/postgres/{connection}");
    var result = await postgres.Query(new(view.Sql, PostgresQuery.DefaultMaxRows, parameters));
    return (result.Columns.Select(c => c.Name).ToArray(), result.Rows.Select(row => row.Select(Cell).ToArray()).ToArray());
}

// An app table is read whole and filtered in memory: equality on each filter column, blank means any.
async Task<(string[] Columns, string[][] Rows)> RunAppTable(View view, List<string> values)
{
    var table = brain.Get<AppTable>(view.Source["apptable:".Length..]);
    var page = await table.Page(0, PostgresQuery.DefaultMaxRows);
    var columns = page.Length == 0 ? [] : page[0].Select(cell => cell.Column).ToArray();
    var rows = page.Select(row => row.Select(cell => Cell(cell.Json)).ToArray()).ToArray();
    foreach (var (column, index) in view.Filters.Select((c, i) => (c, i)))
    {
        var wanted = values[index];
        var position = Array.IndexOf(columns, column);
        if (wanted.Length == 0 || position < 0) { continue; }
        rows = rows.Where(row => row[position] == wanted).ToArray();
    }
    return (columns, rows);
}

// Cells arrive as JSON literals; the person reads plain text.
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

async Task RunView(string id)
{
    try { await Run(await Current(id)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { Console.WriteLine($"run of {id} failed: {error.Message}"); }
}

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds);
