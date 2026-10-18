using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Microsoft.CSharp.Runtime;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var scope = Memory.Scope(brain);
var runs = brain.Get<ITable>(Memory.RunsKey(appKey));
var requests = brain.Get<ITable>(Memory.RequestsKey(appKey));
await Compose();

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    if (await Answered(request)) { continue; }
    try
    {
        await Compose();
        await brain.Get<IText>(appKey + "/apps").Set(Memory.RenderApps(scope, await Memory.Apps(brain), await Memory.Behaviors(brain), await Medians()));
        await brain.Get<IText>(appKey + "/types").Set(Memory.RenderTypes(await brain.Kernel.Types(brain.Stopping)));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/status").Set("Memory could not open: " + error.Message); }
    await app.Request("reindex", "");
    // A ledger that cannot be written only means a redelivery of this request is answered again.
    try { await Memory.Answer(requests, request); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task<bool> Answered(AppRequested request)
{
    try
    {
        await requests.Define(Memory.RequestsDefinition);
        return await Memory.Answered(requests, request);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return false; }
}

// Read the old table only on open, never rewrite it. A failed read retries on the next open.
async Task<Dictionary<string, (string Hash, Memory.Sample[] Recent)>> LoadLegacyRuns()
{
    try { return await Memory.LegacyRuns(runs, brain.Stopping); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return []; }
}

async Task<Dictionary<string, (long P50, int Samples)>> Medians()
{
    var legacyRuns = await LoadLegacyRuns();
    try
    {
        var snapshot = await brain.Get<IBrainRuns>(BrainRunsKeys.For(scope)).Read();
        return Memory.Medians(legacyRuns, snapshot.Statistics);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { return Memory.Medians(legacyRuns, []); }
}

async Task Compose()
{
    var recall = brain.Get<IText>(appKey + "/recall");
    if ((await recall.Read()).Markdown.Length == 0)
    { await recall.Set("Type what you need in the dock's Recall field: Memory lists the synapses that already do it."); }
    var main = brain.Get<ILayout>(appKey + "/main");
    await main.Set(new("list",
    [
        new(UIVocabulary.TextType, appKey + "/status"), new(UIVocabulary.TextType, appKey + "/recall"),
        new(UIVocabulary.TextType, appKey + "/apps"), new(UIVocabulary.TextType, appKey + "/types"),
    ]), (await main.Read()).Revision);
    await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));
}
