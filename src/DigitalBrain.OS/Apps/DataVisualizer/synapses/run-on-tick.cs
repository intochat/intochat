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
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Reminders.Signals;
using AppTable = DigitalBrain.Postgres.Tables.ITable;
using ViewTable = DigitalBrain.Flutter.Table.ITable;
using ViewColumn = DigitalBrain.Flutter.Table.TableColumn;

// When the refresh reminder of a view ticks, that view runs again.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var connection = brain.Setting("Account__db") ?? "";
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var watches = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

while (!brain.Stopping.IsCancellationRequested)
{
    try
    {
        // Prime the watch, then run once: the stream is opening while the first pass runs.
        await using var registryChanges = brain.On<TextChanged>(registry, brain.Stopping).GetAsyncEnumerator(brain.Stopping);
        var next = registryChanges.MoveNextAsync();
        await Reconcile();
        while (await next) { await Reconcile(); next = registryChanges.MoveNextAsync(); }
    }
    catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { }
    catch (Exception error)
    {
        Console.WriteLine($"registry stream stopped, resubscribing: {error.Message}");
        await Task.Delay(TimeSpan.FromSeconds(1), brain.Stopping).ContinueWith(_ => { });
    }
}

async Task Reconcile()
{
    var views = (await Views()).Where(Wanted).ToArray();
    foreach (var gone in watches.Keys.Except(views.Select(v => v.Id)).ToArray())
    {
        watches[gone].Cancel();
        watches[gone].Dispose();
        watches.Remove(gone);
    }
    foreach (var view in views.Where(v => !watches.ContainsKey(v.Id)))
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
        try { await Watch(view, cts.Token); }
        catch { cts.Cancel(); cts.Dispose(); throw; }
        watches[view.Id] = cts;
    }
}

static bool Wanted(View view) => view.RefreshSeconds > 0;

async Task Watch(View view, CancellationToken ct)
{
    var root = $"{appKey}/views/{view.Id}";
    await Pump<ReminderTick>(brain.Get<IReminder>($"{root}/refresh"), view.Id, ct);
}

Task Pump<T>(INeuron source, string id, CancellationToken ct) where T : Signal => Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        try { await foreach (var _ in brain.On<T>(source, ct)) { await RunView(id); } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error) { Console.WriteLine($"{typeof(T).Name} stream of {source} stopped, rewatching: {error.Message}"); }
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { });
        if (!ct.IsCancellationRequested) { try { await RunView(id); } catch (Exception error) { Console.WriteLine($"rerun after rewatch failed: {error.Message}"); } }
    }
}, CancellationToken.None);

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
        await status.Set("Data loaded", $"{rows.Length} rows shown · {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
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
