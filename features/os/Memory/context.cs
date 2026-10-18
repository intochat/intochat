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
// {app}/status and the working memory is left unchanged.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var context = brain.Get<IAgentContext>(Intents.ContextKey(appKey));
var working = brain.Get<IText>(Intents.WorkingKey(appKey));
var status = brain.Get<IText>(appKey + "/status");
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
string[] contracts = [typeof(IIntents).FullName!];
IntentState[] active = [IntentState.Rephrased, IntentState.Confirmed, IntentState.Dispatched, IntentState.Waiting];
var gate = new SemaphoreSlim(1, 1);

var watching = new[]
{
    brain.React<TextChanged>(working, Rebuild, brain.Stopping),
    brain.React<IntentRephrased>(intents, Rebuild, brain.Stopping),
    brain.React<IntentTransitioned>(intents, Rebuild, brain.Stopping),
};

await foreach (var request in brain.On(app, "remember", brain.Stopping))
{
    if (request.Input.Length > Intents.WorkingLimit)
    {
        await status.Set("Memory refused to remember: " + request.Input.Length + " characters is over the " + Intents.WorkingLimit + " limit.");
        continue;
    }
    await working.Set(request.Input);
}
await Task.WhenAll(watching);

async Task Rebuild(CancellationToken ct)
{
    await gate.WaitAsync(ct);
    try
    {
        var entries = (await context.Read()).Entries;
        var text = Intents.RenderWorking((await working.Read()).Markdown);
        await Reconcile(entries, "working-memory", text);
        var live = (await intents.Read()).Intents.Where(intent => active.Contains(intent.State)).ToArray();
        await Reconcile(entries, "active-intents", Intents.RenderActive(live, Intents.ActiveBudget));
    }
    finally { gate.Release(); }
}

async Task Reconcile(ContextEntry[] entries, string name, string text)
{
    var current = entries.FirstOrDefault(entry => entry.Name == name);
    if (text.Length == 0) { if (current is not null) { await context.Remove(name); } return; }
    if (current is null || current.Text != text) { await context.Set(new(name, text, contracts)); }
}
