using System.Linq;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Postgres;
using DigitalBrain.Time.Reminders;
using AppTable = DigitalBrain.Postgres.Tables.ITable;

// The app's operations arrive as requests; each one only writes neurons, never an answer. The
// registry, the composed surfaces and the table/chart neurons are what a caller reads.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation == "refresh") { continue; }
    try { await Answer(request.RequestId, request.Operation, request.Input); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<ICard>(appKey + "/status").Set("Request failed", request.Operation + ": " + error.Message); }
}

async Task Answer(Guid requestId, string operation, string input)
{
    switch (operation)
    {
        case "open":
        {
            var views = await Views.All(brain, appKey);
            foreach (var view in views)
            {
                await Views.Compose(brain, appKey, view);
                await Views.Schedule(brain, appKey, view);
            }
            await Views.ComposeHome(brain, appKey, views);
            if (input != "docked") { await Views.OpenWindowAsync(brain, appKey, brain.Stopping); }
            return;
        }
        case "show":
        {
            var request = Parse(input, "{\"title\", \"kind\": \"table\"|\"chart\", \"source\": \"sql\"|\"apptable:<id>\", \"sql\", \"filters\": [column], \"refreshSeconds\"}");
            var kind = request.GetProperty("kind").GetString() is "chart" ? "chart" : "table";
            var source = request.GetProperty("source").GetString() ?? "sql";
            if (source != "sql" && !source.StartsWith("apptable:", StringComparison.Ordinal)) { throw new ArgumentException("source is sql or apptable:<tableId>."); }
            var sql = request.TryGetProperty("sql", out var s) ? s.GetString() ?? "" : "";
            if (source == "sql" && sql.Length is 0 or > 20_000) { throw new ArgumentException("A SQL view needs 1-20000 characters of SQL."); }
            if (source != "sql") { await RequireOwnTable(source["apptable:".Length..]); }
            var filters = request.TryGetProperty("filters", out var f) ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(16).ToArray() : [];
            // Refresh runs on a durable reminder, whose shortest period is one minute: 0 is off, anything else is 60 s to a day.
            var refresh = request.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var seconds) && seconds > 0 ? Math.Clamp(seconds, 60, 86_400) : 0;
            // The request id makes the view deterministic, so a redelivered request cannot append another view.
            var view = new View("v-" + requestId.ToString("N"), request.GetProperty("title").GetString() ?? "View", kind, source, sql, filters, refresh);
            var existing = await Views.All(brain, appKey);
            view = existing.FirstOrDefault(item => item.Id == view.Id) ?? view;
            // The window opens before the view is registered: a show that fails leaves no listed view without
            // a window, and a redelivered show composes and opens the same view again.
            await Views.Compose(brain, appKey, view);
            await Views.OpenViewWindowAsync(brain, appKey, view, brain.Stopping);
            var views = existing.Any(item => item.Id == view.Id) ? existing : existing.Append(view).ToArray();
            await Views.Save(brain, appKey, views);
            await Views.Schedule(brain, appKey, view);
            await Views.ComposeHome(brain, appKey, views);
            // Which view the request created, readable by whoever asked.
            await brain.Get<IText>(appKey + "/last-view").Set(view.Id);
            await Refresh(view.Id);
            return;
        }
        case "refine":
        {
            var request = Parse(input, "{\"view\", \"filters\": {column: value}, \"filterColumns\": [column], \"sql\", \"linkFiltersTo\"}");
            var id = request.GetProperty("view").GetString() ?? "";
            var views = await Views.All(brain, appKey);
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
                var schema = await brain.Get<IPostgresServer>($"{brain.Setting("Brain")}/connection/postgres/{connection}")
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
            await Views.Compose(brain, appKey, changed);
            await Views.Schedule(brain, appKey, changed);
            await Views.Save(brain, appKey, views.Select(item => item.Id == id ? changed : item).ToArray());
            if (hasValues)
            {
                foreach (var property in values.EnumerateObject())
                {
                    await brain.Get<ITextField>($"{appKey}/views/{changed.FilterGroup ?? changed.Id}/filter/{property.Name}")
                        .SetValue(property.Value.GetString() ?? "");
                }
            }
            await Refresh(id);
            return;
        }
        case "read":
        {
            var id = input.Trim();
            _ = (await Views.All(brain, appKey)).FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            // The table or chart neuron is the result; refreshing it is all a read does.
            await Refresh(id);
            return;
        }
        case "close":
        {
            var id = input.Trim();
            var views = await Views.All(brain, appKey);
            await brain.Get<IReminder>($"{appKey}/views/{id}/refresh").Stop();
            // The window closes before the view leaves the registry, so a close that fails still lists the
            // view and closing it again finishes the job.
            await Views.CloseViewWindowAsync(brain, appKey, id, brain.Stopping);
            var remaining = views.Where(v => v.Id != id).ToArray();
            await Views.Save(brain, appKey, remaining);
            await Views.ComposeHome(brain, appKey, remaining);
            return;
        }
        default: throw new ArgumentException("Unknown operation " + operation);
    }
}

// The operations take JSON; a caller that sent prose learns the shape from the status card.
static JsonElement Parse(string input, string shape)
{
    try { return JsonSerializer.Deserialize<JsonElement>(input); }
    catch (JsonException error) { throw new ArgumentException($"Input is JSON {shape}; {error.Message}", error); }
}

// An app table answers only the app that defined it. Reading one row proves this app owns the source, so a
// table it cannot read never reaches the registry, where run-on-row and run-on-row-deleted would fail to watch it.
async Task RequireOwnTable(string table)
{
    try { _ = await brain.Get<AppTable>(table).Page(0, 1); }
    catch (Exception error) when (error is not OperationCanceledException)
    { throw new ArgumentException($"apptable:{table} is not a table this app defined: {error.Message}", error); }
}

// A refresh is a request of its own: the refresh synapse answers it by writing the table or chart.
async Task Refresh(string view) => await app.Request("refresh", view);
