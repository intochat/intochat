using System.Collections.Concurrent;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// Memory's answer to a message routed.cs or send.cs handed it. Memory writes "ok" under the request id once the
// intent is rephrased (and confirmed when it was sure), or "failed: ..."; each pending request watches that
// text. On "ok" the model answers with the brief: acted on when the intent is Confirmed, shown and asked about
// when it is not. On "failed:", or with no answer within IntendGate seconds, the model answers without a brief.
// A request is claimed (Started) before its turn starts and given back when the turn could not start, so the
// two-second scan tries it again; a claimed request is never answered a second time.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var intents = brain.Get<IIntents>(IIntents.Key(Ino.Scope(brain)));
var gate = new SemaphoreSlim(1, 1);
var watches = new ConcurrentDictionary<Guid, CancellationTokenSource>();
var scanning = Scan();

// IntentRephrased is only a hint that Memory has the request: its answer text is what is acted on.
await foreach (var rephrased in brain.On<IntentRephrased>(intents, brain.Stopping))
{
    if (!Guid.TryParseExact(rephrased.RequestId, "N", out var requestId)) { continue; }
    try { if (await PendingStore.Read(brain, requestId) is { Started: false }) { await EnsureWatch(requestId); } }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, "Could not follow Memory's brief: " + Ino.Clip(error.Message)); }
}
await scanning;

async Task EnsureWatch(Guid requestId)
{
    if (watches.ContainsKey(requestId) || await Ino.MemoryKey(brain) is not { } memoryKey) { return; }
    var stop = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    if (!watches.TryAdd(requestId, stop)) { stop.Dispose(); return; }
    _ = Task.Run(async () =>
    {
        try { await Watch(requestId, memoryKey, stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            // The scan starts a new watch for a request still pending, and answers it once it is stale.
            try { await Ino.Status(brain, "Could not follow Memory's brief: " + Ino.Clip(error.Message)); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        }
        finally { watches.TryRemove(requestId, out _); stop.Dispose(); }
    });
}

// Subscribes first and reads the text inside onSubscribed, so an answer written before the watch is not missed.
async Task Watch(Guid requestId, string memoryKey, CancellationTokenSource stop)
{
    var answer = brain.Get<IText>(memoryKey + "/intents/" + requestId.ToString("N"));
    async Task Check()
    {
        var text = (await answer.Read()).Markdown;
        switch (PendingStore.Classify(text))
        {
            case MemoryAnswer.Ok:
                var key = requestId.ToString("N");
                var intent = (await intents.Read()).Intents.LastOrDefault(candidate => candidate.RequestId == key);
                await Act(requestId, intent?.Brief, intent?.State == IntentState.Confirmed, intent?.Id, null);
                await stop.CancelAsync();
                break;
            case MemoryAnswer.Failed:
                await Act(requestId, null, false, null, "Memory could not intend; answering without a brief");
                await stop.CancelAsync();
                break;
        }
    }
    await foreach (var _ in brain.On<TextChanged>(answer, Check, stop.Token)) { await Check(); }
}

async Task Act(Guid requestId, IntentBrief? brief, bool confirmed, Guid? intentId, string? status)
{
    await gate.WaitAsync(brain.Stopping);
    try
    {
        if (await PendingStore.Read(brain, requestId) is not { Started: false } pending) { return; }
        if (!await PendingStore.Claim(brain, requestId)) { return; }
        if (status is not null) { await Ino.Status(brain, status); }
        var outcome = await Ino.Answer(brain, await app.Read(), pending.Message, pending.Hint, requestId,
            brief, confirmed, await Ino.MemoryKey(brain), intentId);
        if (!PendingStore.KeepsClaim(outcome)) { await PendingStore.Release(brain, requestId); }
    }
    finally { gate.Release(); }
}

// Every two seconds: a pending request is watched, and one Memory has not answered within IntendGate is answered
// without a brief. Entries older than an hour are deleted by PendingStore.Entries.
async Task Scan()
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), brain.Stopping);
            var entries = await PendingStore.Entries(brain);
            var stale = PendingStore.Older(entries, DateTimeOffset.UtcNow, Ino.IntendGate(await app.Read()));
            foreach (var requestId in stale)
            {
                if (watches.TryRemove(requestId, out var watch)) { try { await watch.CancelAsync(); } catch (ObjectDisposedException) { } }
                await Act(requestId, null, false, null, "Memory was slow to intend; answering without a brief");
            }
            foreach (var (requestId, _) in entries.Where(entry => !entry.Value.Started && !stale.Contains(entry.Key)))
            { await EnsureWatch(requestId); }
        }
        catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            try { await Ino.Status(brain, "Could not answer a message Memory was slow to intend: " + Ino.Clip(error.Message)); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        }
    }
}
