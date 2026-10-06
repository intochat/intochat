#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/Modules/Postgres/DigitalBrain.Modules.Postgres.Contracts/DigitalBrain.Modules.Postgres.Contracts.csproj
#:project /brain/src/Modules/Time/DigitalBrain.Modules.Time.Contracts/DigitalBrain.Modules.Time.Contracts.csproj
using System.Globalization;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Table;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.TextField.Signals;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Reminders.Signals;
using AppTable = DigitalBrain.Postgres.Tables.ITable;
using ViewTable = DigitalBrain.Flutter.Table.ITable;
using ViewColumn = DigitalBrain.Flutter.Table.TableColumn;

// The program of every view runs here. Each run is a pure function of (view, filter values, source)
// that replaces the table or chart; the previous content survives a failed run and the status card
// carries the error. Triggers: the registry changing, a filter field changing, an app table row
// changing, a refresh reminder tick.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var registry = brain.Get<IText>(appKey + "/views");
var postgres = brain.Get<IPostgresServer>("default");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var subscriptions = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

await Reconcile();
await foreach (var _ in brain.On<TextChanged>(registry, brain.Stopping)) { await Reconcile(); }

async Task Reconcile()
{
    var views = await Views();
    foreach (var gone in subscriptions.Keys.Except(views.Select(v => v.Id)).ToArray())
    {
        subscriptions[gone].Cancel();
        subscriptions.Remove(gone);
    }
    foreach (var view in views)
    {
        await Run(view);
        if (subscriptions.ContainsKey(view.Id)) { continue; }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
        subscriptions[view.Id] = cts;
        _ = Watch(view, cts.Token);
    }
}

async Task Watch(View view, CancellationToken ct)
{
    var root = $"{appKey}/views/{view.Id}";
    var tasks = new List<Task>();
    foreach (var column in view.Filters)
    {
        tasks.Add(Rerun<TextFieldChanged>(brain.Get<ITextField>($"{root}/filter/{column}"), view.Id, ct));
    }
    if (view.Source.StartsWith("apptable:", StringComparison.Ordinal))
    {
        var table = brain.Get<AppTable>(view.Source["apptable:".Length..]);
        tasks.Add(Rerun<RowUpserted>(table, view.Id, ct));
        tasks.Add(Rerun<RowDeleted>(table, view.Id, ct));
    }
    if (view.RefreshSeconds > 0)
    {
        tasks.Add(Rerun<ReminderTick>(brain.Get<IReminder>($"{root}/refresh"), view.Id, ct));
    }
    await Task.WhenAll(tasks);
}

// A watcher that fails exits alone; the runner loop and the other views keep going.
async Task Rerun<T>(INeuron source, string id, CancellationToken ct) where T : Signal
{
    try
    {
        await foreach (var _ in brain.On<T>(source, ct)) { await Run(await Current(id)); }
    }
    catch (OperationCanceledException) { }
    catch (Exception error) { Console.WriteLine($"watcher for {id} stopped: {error.Message}"); }
}

async Task Run(View? view)
{
    if (view is null) { return; }
    var root = $"{appKey}/views/{view.Id}";
    var status = brain.Get<ICard>($"{root}/status");
    try
    {
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

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds);
