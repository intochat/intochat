using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;

// Memory's answer to a message routed.cs or send.cs handed it. When Memory rephrases the message into a
// brief, the model answers with that brief: acted on when Memory confirmed it, shown to the person and asked
// about when it did not. A pending request whose brief has not come within IntendGate seconds is answered
// without one. Each pending request is answered once: a late signal or a second scan finds it started.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var intents = brain.Get<IIntents>(IIntents.Key(Ino.Scope(brain)));
var gate = new SemaphoreSlim(1, 1);
var scanning = Scan();

await foreach (var rephrased in brain.On<IntentRephrased>(intents, brain.Stopping))
{
    if (!Guid.TryParseExact(rephrased.RequestId, "N", out var requestId)) { continue; }
    try { await Briefed(requestId, rephrased.IntentId); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, "Could not answer with Memory's brief: " + Ino.Clip(error.Message)); }
}
await scanning;

async Task Briefed(Guid requestId, Guid intentId)
{
    if (await PendingStore.Read(brain, requestId) is not { Started: false }) { return; }
    var memoryKey = await Ino.MemoryKey(brain);
    // Memory confirms right after it rephrases and writes "ok" under the request id once both are done,
    // so the intent is read after that answer (or a short wait), never between the two writes.
    if (memoryKey is not null)
    {
        var answer = brain.Get<IText>(memoryKey + "/intents/" + requestId.ToString("N"));
        for (var wait = 0; wait < 20 && (await answer.Read()).Markdown.Length == 0; wait++)
        { await Task.Delay(TimeSpan.FromMilliseconds(100), brain.Stopping); }
    }
    var intent = (await intents.Read()).Intents.FirstOrDefault(candidate => candidate.Id == intentId);
    await gate.WaitAsync(brain.Stopping);
    try
    {
        if (await PendingStore.Read(brain, requestId) is not { Started: false } pending) { return; }
        await Ino.Answer(brain, await app.Read(), pending.Message, pending.Hint, requestId,
            intent?.Brief, intent?.State == IntentState.Confirmed, memoryKey, intent?.Id);
        await PendingStore.MarkStarted(brain, requestId);
    }
    finally { gate.Release(); }
}

// Every two seconds: a request Memory has not briefed within IntendGate is answered without a brief.
async Task Scan()
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), brain.Stopping);
            var snapshot = await app.Read();
            foreach (var requestId in await PendingStore.Older(brain, Ino.IntendGate(snapshot)))
            {
                await gate.WaitAsync(brain.Stopping);
                try
                {
                    if (await PendingStore.Read(brain, requestId) is not { Started: false } pending) { continue; }
                    await Ino.Status(brain, "Memory was slow to intend; answering without a brief");
                    await Ino.Answer(brain, snapshot, pending.Message, pending.Hint, requestId, memoryKey: await Ino.MemoryKey(brain));
                    await PendingStore.MarkStarted(brain, requestId);
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            try { await Ino.Status(brain, "Could not answer a message Memory was slow to intend: " + Ino.Clip(error.Message)); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        }
    }
}
