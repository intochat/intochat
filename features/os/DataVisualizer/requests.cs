using System.Globalization;
using System.Linq;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Time.Reminders;
using AppTable = IntoChat.OS.Modules.Postgres.Tables.ITable;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

// The app's operations arrive as requests; each one updates the declarative view surface.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await Views.ComposeHome(brain, appKey, await Views.All(brain, appKey));

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation == "refresh") { continue; }
    try { await Answer(request.RequestId, request.Operation, request.Input); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Views.SetStatus(brain, appKey, "Request failed", request.Operation + ": " + error.Message); }
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
            var refresh = request.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var seconds) && seconds > 0 ? Math.Clamp(seconds, 60, 86_400) : 0;
            var view = new DataViewDefinition("v-" + requestId.ToString("N"), request.GetProperty("title").GetString() ?? "View", kind, source, sql, filters, refresh);
            var existing = await Views.All(brain, appKey);
            view = existing.FirstOrDefault(item => item.Id == view.Id) ?? view;
            await Views.Compose(brain, appKey, view);
            var views = existing.Any(item => item.Id == view.Id) ? existing : existing.Append(view).ToArray();
            await Views.Save(brain, appKey, views);
            await Views.Schedule(brain, appKey, view);
            await Views.ComposeHome(brain, appKey, views);
            var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
            await new ViewPublisher(brain.Get<IView>(appKey + "/last-view"), revision).Publish(ViewUi.Text("last-view", view.Id));
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
            await Views.Compose(brain, appKey, changed);
            await Views.Schedule(brain, appKey, changed);
            await Views.Save(brain, appKey, views.Select(item => item.Id == id ? changed : item).ToArray());
            if (hasValues)
            {
                foreach (var property in values.EnumerateObject())
                {
                    await Views.SetFilterValue(brain, $"{appKey}/views/{changed.FilterGroup ?? changed.Id}/filter/{property.Name}", property.Value.GetString() ?? "");
                }
            }
            await Refresh(id);
            return;
        }
        case "read":
        {
            var id = input.Trim();
            _ = (await Views.All(brain, appKey)).FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("No view " + id);
            await Refresh(id);
            return;
        }
        case "close":
        {
            var id = input.Trim();
            var views = await Views.All(brain, appKey);
            await brain.Get<IReminder>($"{appKey}/views/{id}/refresh").Stop();
            var remaining = views.Where(v => v.Id != id).ToArray();
            await Views.Save(brain, appKey, remaining);
            await Views.ComposeHome(brain, appKey, remaining);
            return;
        }
        default: throw new ArgumentException("Unknown operation " + operation);
    }
}

static JsonElement Parse(string input, string shape)
{
    try { return JsonSerializer.Deserialize<JsonElement>(input); }
    catch (JsonException error) { throw new ArgumentException($"Input is JSON {shape}; {error.Message}", error); }
}

async Task RequireOwnTable(string table)
{
    try { _ = await brain.Get<AppTable>(table).Page(0, 1); }
    catch (Exception error) when (error is not OperationCanceledException)
    { throw new ArgumentException($"apptable:{table} is not a table this app defined: {error.Message}", error); }
}

async Task Refresh(string view) => await app.Request("refresh", view);
