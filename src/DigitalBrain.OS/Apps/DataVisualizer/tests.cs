#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/Modules/Postgres/DigitalBrain.Modules.Postgres.Contracts/DigitalBrain.Modules.Postgres.Contracts.csproj
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Table;
using AppTable = DigitalBrain.Postgres.Tables.ITable;
using ViewTable = DigitalBrain.Flutter.Table.ITable;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Postgres.Tables;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var failures = 0;
const string Values = "select * from (values ('us', 2), ('uk', 1)) as t(country, n)";

await Scenario("Showing a SQL view opens a window with the rows", async () =>
{
    var shown = await Show(new { title = "People", kind = "table", source = "sql", sql = Values, filters = Array.Empty<string>() });
    var id = shown.GetProperty("view").GetString()!;
    try
    {
        var surface = shown.GetProperty("window").GetProperty("surface").GetString()!;
        if ((await brain.Get<ISurface>(surface).Read()).Definition.Title != "People") { throw new InvalidOperationException("The window surface is not titled after the view."); }
        var table = await WaitTable(id, rows => rows.Count == 2);
        if (table.Columns.Select(c => c.Id).SequenceEqual(["country", "n"]) is false) { throw new InvalidOperationException("Columns were not country, n."); }
    }
    finally { await Ask("close", id); }
});

await Scenario("Typing a filter value replaces the rows", async () =>
{
    var shown = await Show(new { title = "People", kind = "table", source = "sql", sql = Values + " where country = $1", filters = new[] { "country" } });
    var id = shown.GetProperty("view").GetString()!;
    try
    {
        var filter = brain.Get<ITextField>($"{appKey}/views/{id}/filter/country");
        await filter.Input("uk");
        var table = await WaitTable(id, rows => rows.Count == 1 && rows[0][0] == "uk");
        await filter.Input("u's; drop table t");
        await WaitTable(id, rows => rows.Count == 0);
        var status = (await brain.Get<ICard>($"{appKey}/views/{id}/status").Read()).Body;
        if (status.Contains("error", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("A quoted filter value raised an error: " + status); }
    }
    finally { await Ask("close", id); }
});

await Scenario("A chart view renders points from an aggregate query", async () =>
{
    var shown = await Show(new { title = "Share", kind = "chart", source = "sql", sql = "select country, sum(n) from (values ('us', 2), ('us', 3), ('uk', 1)) as t(country, n) group by 1 order by 1", filters = Array.Empty<string>() });
    var id = shown.GetProperty("view").GetString()!;
    try
    {
        var chart = await Wait(() => brain.Get<IChart>($"{appKey}/views/{id}/chart").Read(), state => state.Points.Count == 2);
        if (chart.Points[0].Label != "uk" || chart.Points[0].Value != 1 || chart.Points[1].Value != 5) { throw new InvalidOperationException("The chart points are not uk→1, us→5."); }
    }
    finally { await Ask("close", id); }
});

await Scenario("Changing an app table re-renders its view", async () =>
{
    var sample = brain.Get<AppTable>(appKey + "/sample");
    await sample.Define(new([new("id", "text"), new("country", "text")], ["id"]));
    await sample.Upsert([new("id", "\"1\"")], [new("country", "\"us\"")]);
    var shown = await Show(new { title = "Sample", kind = "table", source = "apptable:" + appKey + "/sample", sql = "", filters = Array.Empty<string>() });
    var id = shown.GetProperty("view").GetString()!;
    try
    {
        await WaitTable(id, rows => rows.Count == 1);
        await sample.Upsert([new("id", "\"2\"")], [new("country", "\"uk\"")]);
        await WaitTable(id, rows => rows.Count == 2);
    }
    finally { await Ask("close", id); await sample.Delete([new("id", "\"1\"")]); await sample.Delete([new("id", "\"2\"")]); }
});

await Scenario("Restarting the behaviors rebuilds the same windows", async () =>
{
    var a = (await Show(new { title = "A", kind = "table", source = "sql", sql = Values, filters = Array.Empty<string>() })).GetProperty("view").GetString()!;
    var b = (await Show(new { title = "B", kind = "table", source = "sql", sql = Values, filters = Array.Empty<string>() })).GetProperty("view").GetString()!;
    try
    {
        var first = JsonDocument.Parse(await Ask("open", "")).RootElement.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToArray();
        var revision = (await brain.Get<ISurface>($"{appKey}/views/{a}/surface").Read()).Revision;
        var second = JsonDocument.Parse(await Ask("open", "")).RootElement.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToArray();
        if (!first.SequenceEqual(second) || !first.Contains(a) || !first.Contains(b)) { throw new InvalidOperationException("Reopening changed the view list."); }
        var after = (await brain.Get<ISurface>($"{appKey}/views/{a}/surface").Read()).Revision;
        if (after > revision + 1) { throw new InvalidOperationException("Reopening recomposed a view more than once."); }
    }
    finally { await Ask("close", a); await Ask("close", b); }
});

await Scenario("An unreachable source keeps the last rows and reports the error", async () =>
{
    var shown = await Show(new { title = "People", kind = "table", source = "sql", sql = Values, filters = Array.Empty<string>() });
    var id = shown.GetProperty("view").GetString()!;
    try
    {
        await WaitTable(id, rows => rows.Count == 2);
        await Ask("refine", JsonSerializer.Serialize(new { view = id, sql = "select * from table_that_does_not_exist_9f3" }));
        var status = await Wait(() => brain.Get<ICard>($"{appKey}/views/{id}/status").Read(), card => card.Body.Length > 0 && !card.Body.StartsWith("ok", StringComparison.Ordinal));
        if ((await brain.Get<ViewTable>($"{appKey}/views/{id}/table").Read()).Rows.Count != 2) { throw new InvalidOperationException("The previous rows were lost."); }
    }
    finally { await Ask("close", id); }
});

await Scenario("Closing a view removes it", async () =>
{
    var id = (await Show(new { title = "Gone", kind = "table", source = "sql", sql = Values, filters = Array.Empty<string>() })).GetProperty("view").GetString()!;
    await Ask("close", id);
    try { await Ask("read", id); throw new InvalidOperationException("Reading a closed view succeeded."); }
    catch (InvalidOperationException error) when (error.Message.Contains("No view", StringComparison.Ordinal)) { }
});

await Scenario("(live) A platform table shows live data", async () =>
{
    var id = (await Show(new { title = "Tables", kind = "table", source = "sql", sql = "select table_name from information_schema.tables limit 5", filters = Array.Empty<string>() })).GetProperty("view").GetString()!;
    try { await WaitTable(id, rows => rows.Count == 5); }
    finally { await Ask("close", id); }
});

return failures == 0 ? 0 : 1;

async Task<JsonElement> Show(object view) => JsonDocument.Parse(await Ask("show", JsonSerializer.Serialize(view))).RootElement;

async Task<TableState> WaitTable(string id, Func<List<List<string>>, bool> ready)
    => await Wait(() => brain.Get<ViewTable>($"{appKey}/views/{id}/table").Read(), state => ready(state.Rows));

// Views render asynchronously after a signal; a scenario waits for the state it expects, bounded.
async Task<T> Wait<T>(Func<Task<T>> read, Func<T, bool> ready)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (true)
    {
        var state = await read();
        if (ready(state)) { return state; }
        if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException("The view did not reach the expected state."); }
        await Task.Delay(200);
    }
}

async Task Scenario(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"dbtest:pass {name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"dbtest:fail {name}\t{error.Message.ReplaceLineEndings(" ")}"); }
}

async Task<string> Ask(string operation, string input)
{
    var invocation = await app.Invoke(new InvokeApp(Guid.NewGuid(), operation, input));
    var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    while (invocation.Status == InvocationStatus.Pending)
    {
        if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException("The app did not answer within 5 minutes."); }
        await Task.Delay(200);
        invocation = await app.ReadInvocation(invocation.Id);
    }
    if (invocation.Status == InvocationStatus.Failed) { throw new InvalidOperationException(invocation.Error ?? "The app failed."); }
    return invocation.Output ?? "";
}
