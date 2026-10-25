using IntoChat.OS.Modules.AI.Agents.Facts;
using IntoChat.OS.Modules.AI.Agents.Facts.Signals;
using IntoChat.OS.Modules.Flutter.Text;

// The one writer of facts into the life index: whoever asserts a fact (consolidation, an accepted review item, the
// model through IFacts), FactAsserted indexes it as "fact:<id>" (subject and text, its kind, validity start and
// importance), read from the facts neuron and only while it is current; FactForgotten and FactInvalidated remove
// "fact:<id>". Upsert and Remove are idempotent, so a redelivered signal changes nothing. One gate orders the three
// watchers, so a fact forgotten right after it was asserted is never left in the index.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var facts = brain.Get<IFacts>(IFacts.Key(Memory.Scope(brain)));
var status = brain.Get<IText>(appKey + "/status");
var gate = new SemaphoreSlim(1, 1);

// The removals are supervised side loops, as in context.cs: a fault is reported on {app}/status and the loop restarts.
var removing = new[]
{
    Supervise("forgotten facts", async ct => { await foreach (var gone in brain.On<FactForgotten>(facts, ct)) { await Remove(gone.FactId); } }),
    Supervise("invalidated facts", async ct => { await foreach (var ended in brain.On<FactInvalidated>(facts, ct)) { await Remove(ended.FactId); } }),
};

await foreach (var asserted in brain.On<FactAsserted>(facts, brain.Stopping))
{
    await gate.WaitAsync(brain.Stopping);
    try
    {
        var fact = (await facts.Read()).Facts.FirstOrDefault(candidate => candidate.Id == asserted.FactId);
        if (fact is null || fact.ValidTo is not null || fact.Hidden) { continue; }
        await Recall.Index(brain, "fact:" + fact.Id.ToString("D"), "fact", (fact.Subject.Length > 0 ? fact.Subject + ": " : "") + fact.Text,
            fact.ValidFrom, fact.Importance, fact.Kind.ToString().ToLowerInvariant());
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Report("Memory could not index a fact: " + error.Message); }
    finally { gate.Release(); }
}
await Task.WhenAll(removing);

async Task Remove(Guid factId)
{
    await gate.WaitAsync(brain.Stopping);
    try { await Recall.Forget(brain, factId.ToString("D")); }
    finally { gate.Release(); }
}

async Task Supervise(string name, Func<CancellationToken, Task> watch)
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        string problem;
        try { await watch(brain.Stopping); problem = "ended"; }
        catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
        catch (Exception error) { problem = "failed: " + error.Message; }
        await Report("Memory's index watcher of " + name + " " + problem + "; restarting.");
        try { await Task.Delay(TimeSpan.FromSeconds(5), brain.Stopping); }
        catch (OperationCanceledException) { return; }
    }
}

async Task Report(string text)
{
    try { await status.Set(text.Length <= 200 ? text : text[..200]); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}
