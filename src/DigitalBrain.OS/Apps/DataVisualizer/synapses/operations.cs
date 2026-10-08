using System.Text.Json;
using System.Globalization;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Postgres;


// Every window is derived from the registry, so composing is
// idempotent: the same view composes the same neurons with the same content.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

await foreach (var _ in brain.On<AppInvoked>(app, Reconcile, brain.Stopping)) { await Reconcile(); }

async Task Reconcile()
{
    foreach (var request in (await app.Pending()).Where(item => item.Operation != "refresh"))
    {
        try { await app.Respond(new(request.Id, await Answer(request.Id, request.Operation, request.Input), null)); }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await app.Respond(new(request.Id, null, error.Message)); }
    }
}

async Task<string> Answer(Guid invocationId, string operation, string input)
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
            var filters = request.TryGetProperty("filters", out var f) ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(16).ToArray() : [];
            var refresh = request.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 0, 86_400) : 0;
            var view = new View("v-" + invocationId.ToString("N"), request.GetProperty("title").GetString() ?? "View", kind, source, sql, filters, refresh);
            var existing = await Views();
            var views = existing.Any(item => item.Id == view.Id) ? existing : existing.Append(view).ToArray();
            await Compose(view);
            await Save(views);
            await ComposeHome(views);
            await Refresh(view.Id);
            return JsonSerializer.Serialize(new { view = view.Id, window = new { id = $"{appKey}/views/{view.Id}/window", title = view.Title, surface = $"{appKey}/views/{view.Id}/surface" } }, json);
        }
        case "refine":
        {
            var request = JsonSerializer.Deserialize<JsonElement>(input);
            var id = request.GetProperty("view").GetString() ?? "";
            var views = await Views();
            var view = views.FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            var changed = view;
            if (request.TryGetProperty("sql", out var sql))
            {
                var text = sql.GetString() ?? view.Sql;
                if (view.Source == "sql" && text.Length is 0 or > 20_000) { throw new ArgumentException("A SQL view needs 1-20000 characters of SQL."); }
                changed = changed with { Sql = text };
            }
            var hasValues = request.TryGetProperty("filters", out var values);
            if (hasValues)
            {
                if (values.ValueKind != JsonValueKind.Object || values.EnumerateObject().Any(property => property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                { throw new ArgumentException("Filter values must be strings or null (clear)."); }
                var columns = view.Filters.Concat(values.EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal).ToArray();
                if (columns.Length > 16 || columns.Any(column => string.IsNullOrWhiteSpace(column) || column.Length > 128))
                { throw new ArgumentException("Use at most 16 nonempty output-column filters, each at most 128 characters."); }
                changed = changed with { Filters = columns };
            }
            if (request.TryGetProperty("filterColumns", out var filterColumns))
            {
                if (filterColumns.ValueKind != JsonValueKind.Array || filterColumns.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                { throw new ArgumentException("filterColumns must be an array of output-column names."); }
                var columns = filterColumns.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                if (columns.Length > 16 || columns.Any(column => string.IsNullOrWhiteSpace(column) || column.Length > 128)
                    || (hasValues && values.EnumerateObject().Any(property => !columns.Contains(property.Name))))
                { throw new ArgumentException("Use at most 16 valid filter columns and supply values only for those columns."); }
                changed = changed with { Filters = columns };
            }
            if (changed.Source == "sql" && changed.Filters.Length > 0)
            {
                var connection = brain.Setting("Account__db") ?? "";
                if (connection.Length == 0) { throw new InvalidOperationException("Connect a Postgres database to this app."); }
                var sourceSql = changed.Sql.Trim().TrimEnd(';');
                var schema = await brain.Get<IPostgresServer>($"connection/{brain.Setting("Brain")}/postgres/{connection}")
                    .Query(new($"select * from ({sourceSql}) as filter_source limit 0", 1));
                if (changed.Filters.Any(column => !schema.Columns.Any(output => output.Name == column)))
                { throw new ArgumentException("Every filter must name an output column of the view query."); }
            }
            if (request.TryGetProperty("linkFiltersTo", out var link))
            {
                var sourceId = link.GetString();
                var source = views.FirstOrDefault(item => item.Id == sourceId) ?? throw new ArgumentException("The filter source view does not exist.");
                if (changed.Filters.Any(column => !source.Filters.Contains(column)))
                { throw new ArgumentException("Linked filters must name output columns declared by the source view."); }
                changed = changed with { FilterGroup = source.FilterGroup ?? source.Id };
            }
            // Composition only configures fields; it preserves their current values.
            await Compose(changed);
            await Save(views.Select(item => item.Id == id ? changed : item).ToArray());
            if (hasValues)
            {
                foreach (var property in values.EnumerateObject())
                {
                    await brain.Get<ITextField>($"{appKey}/views/{changed.FilterGroup ?? changed.Id}/filter/{property.Name}")
                        .SetValue(property.Value.GetString() ?? "");
                }
            }
            await Refresh(id);
            return JsonSerializer.Serialize(new { view = id }, json);
        }
        case "read":
        {
            var id = input.Trim();
            var view = (await Views()).FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            await Refresh(id);
            var status = await brain.Get<ICard>($"{appKey}/views/{id}/status").Read();
            if (view.Kind == "chart")
            {
                var chart = await brain.Get<DigitalBrain.Flutter.Chart.IChart>($"{appKey}/views/{id}/chart").Read();
                return JsonSerializer.Serialize(new { view = id, kind = "chart", chart.Title, chart.Points, status }, json);
            }
            var table = await brain.Get<DigitalBrain.Flutter.Table.ITable>($"{appKey}/views/{id}/table").Read();
            return JsonSerializer.Serialize(new { view = id, columns = table.Columns.Select(c => c.Id), rows = table.Rows, status }, json);
        }
        case "close":
        {
            var id = input.Trim();
            var views = await Views();

            await brain.Get<IReminder>($"{appKey}/views/{id}/refresh").Stop();
            await Save(views.Where(v => v.Id != id).ToArray());
            await ComposeHome(views.Where(v => v.Id != id).ToArray());
            var workspace = brain.Get<IWorkspace>(brain.Setting("Brain")!);
            var current = await workspace.Read();
            if (current.Windows.Any(window => window.IsOpen && window.Id == $"{appKey}/views/{id}/window"))
            { await workspace.Close($"{appKey}/views/{id}/window", current.Revision); }
            return JsonSerializer.Serialize(new { closed = id }, json);
        }
        default: throw new ArgumentException("Unknown operation " + operation);
    }
}

async Task Refresh(string view)
{
    var id = Guid.NewGuid();
    await app.Invoke(new(id, "refresh", view));
    var response = await app.Await(id);
    if (response.Error is not null) { throw new InvalidOperationException(response.Error); }
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
        await brain.Get<ITextField>($"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}").Configure(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(column.Replace('_', ' ')), "text");
        children.Add(new("textfield", $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
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

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds, string? FilterGroup = null);
