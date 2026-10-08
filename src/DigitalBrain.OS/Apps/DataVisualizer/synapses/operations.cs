using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Time.Reminders;


// Every window is derived from the registry, so composing is
// idempotent: the same view composes the same neurons with the same content.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

await foreach (var invoked in app.Invocations(brain, brain.Stopping))
{
    try { await app.Respond(new AppResponse(invoked.InvocationId, await Answer(invoked.Operation, invoked.Input), null)); }
    catch (Exception error) { await app.Respond(new AppResponse(invoked.InvocationId, null, error.Message)); }
}

async Task<string> Answer(string operation, string input)
{
    switch (operation)
    {
        case "open":
        {
            var views = await Views();
            foreach (var view in views) { await Compose(view); }
            await ComposeHome(views);
            return JsonSerializer.Serialize(new { id = appKey + "/window", title = "Data Visualizer", surface = appKey + "/surface", views = views.Select(v => new { v.Id, v.Title, v.Kind }) }, json);
        }
        case "show":
        {
            var request = JsonSerializer.Deserialize<JsonElement>(input);
            var kind = request.GetProperty("kind").GetString() is "chart" ? "chart" : "table";
            var source = request.GetProperty("source").GetString() ?? "sql";
            if (source != "sql" && !source.StartsWith("apptable:", StringComparison.Ordinal)) { throw new ArgumentException("source is sql or apptable:<tableId>."); }
            var sql = request.TryGetProperty("sql", out var s) ? s.GetString() ?? "" : "";
            if (source == "sql" && sql.Length is 0 or > 20_000) { throw new ArgumentException("A SQL view needs 1-20000 characters of SQL."); }
            var filters = request.TryGetProperty("filters", out var f) ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(16).ToArray() : [];
            var refresh = request.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 0, 86_400) : 0;
            var view = new View("v-" + Guid.NewGuid().ToString("N")[..12], request.GetProperty("title").GetString() ?? "View", kind, source, sql, filters, refresh);
            var views = (await Views()).Append(view).ToArray();
            await Compose(view);
            await Save(views);
            await ComposeHome(views);
            return JsonSerializer.Serialize(new { view = view.Id, window = new { id = $"{appKey}/views/{view.Id}/window", title = view.Title, surface = $"{appKey}/views/{view.Id}/surface" } }, json);
        }
        case "refine":
        {
            var request = JsonSerializer.Deserialize<JsonElement>(input);
            var id = request.GetProperty("view").GetString() ?? "";
            var views = await Views();
            var view = views.FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            if (request.TryGetProperty("filters", out var values))
            {
                foreach (var property in values.EnumerateObject())
                {
                    if (view.Filters.Contains(property.Name)) { await brain.Get<ITextField>($"{appKey}/views/{id}/filter/{property.Name}").SetValue(property.Value.GetString() ?? ""); }
                }
            }
            if (request.TryGetProperty("sql", out var sql))
            {
                var changed = view with { Sql = sql.GetString() ?? view.Sql };
                await Save(views.Select(v => v.Id == id ? changed : v).ToArray());
            }
            else
            {
                // Filter fields changed, nothing in the registry did; a registry write is still the one
                // signal the runner listens to.
                await Save(views);
            }
            return JsonSerializer.Serialize(new { view = id }, json);
        }
        case "read":
        {
            var id = input.Trim();
            _ = (await Views()).FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            var table = await brain.Get<DigitalBrain.Flutter.Table.ITable>($"{appKey}/views/{id}/table").Read();
            return JsonSerializer.Serialize(new { view = id, columns = table.Columns.Select(c => c.Id), rows = table.Rows }, json);
        }
        case "close":
        {
            var id = input.Trim();
            var views = await Views();
            if (views.All(v => v.Id != id)) { throw new InvalidOperationException("No view " + id); }
            await brain.Get<IReminder>($"{appKey}/views/{id}/refresh").Stop();
            await Save(views.Where(v => v.Id != id).ToArray());
            await ComposeHome(views.Where(v => v.Id != id).ToArray());
            return JsonSerializer.Serialize(new { closed = id }, json);
        }
        default: throw new ArgumentException("Unknown operation " + operation);
    }
}

async Task<View[]> Views()
{
    var text = (await registry.Read()).Markdown;
    return text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, json) ?? [];
}

Task Save(View[] views) => registry.Set(JsonSerializer.Serialize(views, json));

async Task Compose(View view)
{
    var root = $"{appKey}/views/{view.Id}";
    var children = new List<UiChildRef>();
    foreach (var column in view.Filters)
    {
        await brain.Get<ITextField>($"{root}/filter/{column}").Configure(column, "text");
        children.Add(new("textfield", $"{root}/filter/{column}"));
    }
    children.Add(new("card", $"{root}/status"));
    children.Add(new(view.Kind, $"{root}/{view.Kind}"));
    var layout = brain.Get<ILayout>($"{root}/layout");
    var current = await layout.Read();
    var definition = new LayoutDefinition("list", children.ToArray());
    if (current.Definition.Mode != definition.Mode || !Same(current.Definition.Children, definition.Children)) { await layout.Set(definition, current.Revision); }
    var surface = brain.Get<ISurface>($"{root}/surface");
    var state = await surface.Read();
    if (state.Definition.Title != view.Title || state.Definition.Children.Length != 1) { await surface.Set(new(view.Title, [new("layout", $"{root}/layout")]), state.Revision); }
    if (view.RefreshSeconds > 0) { await brain.Get<IReminder>($"{root}/refresh").Start(TimeSpan.FromSeconds(view.RefreshSeconds), TimeSpan.FromSeconds(view.RefreshSeconds)); }
}

async Task ComposeHome(View[] views)
{
    var list = brain.Get<ICard>(appKey + "/list");
    await list.Set("Views", views.Length == 0 ? "No views yet. Ask the assistant to show a table or a chart." : string.Join("\n", views.Select(v => $"- {v.Title} ({v.Kind}, {v.Id})")));
    var main = brain.Get<ILayout>(appKey + "/main");
    var current = await main.Read();
    if (current.Definition.Children.Length == 0) { await main.Set(new("column", [new("card", appKey + "/list")]), current.Revision); }
    var surface = brain.Get<ISurface>(appKey + "/surface");
    var state = await surface.Read();
    if (state.Definition.Title != "Data Visualizer") { await surface.Set(new("Data Visualizer", [new("layout", appKey + "/main")]), state.Revision); }
}

static bool Same(UiChildRef[] left, UiChildRef[] right) => left.Length == right.Length && left.Zip(right).All(pair => pair.First == pair.Second);

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds);
