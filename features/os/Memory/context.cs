using IntoChat.OS.AI.Agents.Context;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// Memory owns the agent context Ino attaches to its turns and keeps two entries current: "working-memory"
// (the text of {app}/working, cut at 4,000 characters; removed when empty) and "active-intents" (the
// Rephrased, Confirmed, Dispatched and Waiting intents, newest first, within 6,000 characters). Each
// rebuild reads both sources and writes an entry only when its text differs from the one the context holds.
// "remember" replaces the working memory with its input; input over 4,000 characters is refused on
// {app}/remember-status and the working memory is left unchanged.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var context = brain.Get<IAgentContext>(Intents.ContextKey(appKey));
var working = brain.Get<IText>(Intents.WorkingKey(appKey));
var status = brain.Get<IText>(appKey + "/status");
var rememberStatus = brain.Get<IText>(appKey + "/remember-status");
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
string[] contracts = [typeof(IIntents).FullName!];
var gate = new SemaphoreSlim(1, 1);

// Each watcher is supervised: if its loop faults or ends before the brain stops, say so on {app}/status and
// start it again, so the context never goes quietly stale while "remember" keeps being served.
var watching = new[]
{
    Supervise("working memory", ct => brain.React<TextChanged>(working, Rebuild, ct)),
    Supervise("rephrased intents", ct => brain.React<IntentRephrased>(intents, Rebuild, ct)),
    Supervise("intent transitions", ct => brain.React<IntentTransitioned>(intents, Rebuild, ct)),
};

await foreach (var request in brain.On(app, "remember", brain.Stopping))
{
    if (request.Input.Length > Intents.WorkingLimit)
    {
        await rememberStatus.Set("Memory refused to remember: " + request.Input.Length + " characters is over the " + Intents.WorkingLimit + " limit.");
        continue;
    }
    await working.Set(request.Input);
}
await Task.WhenAll(watching);

async Task Supervise(string name, Func<CancellationToken, Task> watch)
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        string problem;
        try { await watch(brain.Stopping); problem = "ended"; }
        catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
        catch (Exception error) { problem = "failed: " + error.Message; }
        try { await status.Set("Memory's watcher of " + name + " " + problem + "; restarting."); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        try { await Task.Delay(TimeSpan.FromSeconds(5), brain.Stopping); }
        catch (OperationCanceledException) { return; }
    }
}

async Task Rebuild(CancellationToken ct)
{
    await gate.WaitAsync(ct);
    try
    {
        var entries = (await context.Read()).Entries;
        var text = Intents.RenderWorking((await working.Read()).Markdown);
        await Reconcile(entries, "working-memory", text);
        var live = Intents.ActiveOf((await intents.Read()).Intents);
        await Reconcile(entries, "active-intents", Intents.RenderActive(live, Intents.ActiveBudget));
    }
    catch (Exception error) when (!ct.IsCancellationRequested && !brain.Stopping.IsCancellationRequested)
    {
        try { await status.Set("Memory could not refresh the agent context: " + error.Message); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
    finally { gate.Release(); }
}

async Task Reconcile(ContextEntry[] entries, string name, string text)
{
    var current = entries.FirstOrDefault(entry => entry.Name == name);
    switch (Intents.Decide(current?.Text, text))
    {
        case Intents.Change.Remove: await context.Remove(name); break;
        case Intents.Change.Set: await context.Set(new(name, text, contracts)); break;
    }
}
