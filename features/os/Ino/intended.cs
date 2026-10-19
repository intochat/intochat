using System.Collections.Concurrent;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// Memory's answer to a message routed.cs or send.cs handed it. Memory writes "ok <intent id>" under the request id
// once the intent is rephrased (and confirmed when it was sure), or "failed: ..."; each pending request watches
// that text. On "ok" the model answers with the brief of the intent the answer names (a continued intent keeps
// its first request id, so the id is what finds it): acted on when the intent is Confirmed, shown and asked about
// when it is not. On "failed:", or with no answer within IntendGate seconds, the model answers without a brief.
// A request is claimed (Started) before its turn starts and given back when the turn could not start; the
// two-second scan tries it again after a backoff (2 s, 4 s), reading Memory's answer afresh, and after
// PendingStore.MaxAttempts it gives up and leaves the entry for the one-hour prune.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var intents = brain.Get<IIntents>(IIntents.Key(Ino.Scope(brain)));
var gate = new SemaphoreSlim(1, 1);
var watches = new ConcurrentDictionary<Guid, CancellationTokenSource>();
var scanning = Scan();

// IntentRephrased is only a hint that Memory has the request: its answer text is what is acted on. A continued
// intent's signal carries its first request id, so a hint that names no waiting request watches every one.
await foreach (var rephrased in brain.On<IntentRephrased>(intents, brain.Stopping))
{
    try
    {
        if (Guid.TryParseExact(rephrased.RequestId, "N", out var requestId)
            && await PendingStore.Read(brain, requestId) is { Started: false, Attempts: 0 }) { await EnsureWatch(requestId); continue; }
        var now = DateTimeOffset.UtcNow;
        var gateTime = Ino.IntendGate(await app.Read());
        foreach (var (pendingId, pending) in await PendingStore.Entries(brain))
        { if (PendingStore.Decide(pending, now, gateTime) == PendingStep.Watch) { await EnsureWatch(pendingId); } }
    }
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
        if (await Answer(requestId, memoryKey, null)) { await stop.CancelAsync(); }
    }
    await foreach (var _ in brain.On<TextChanged>(answer, Check, stop.Token)) { await Check(); }
}

// Answers from Memory's answer text: with the brief on "ok", without it on "failed:"; with no answer yet, only
// when a fallback status is given (the gate has passed, or a released request is tried again). True when acted.
async Task<bool> Answer(Guid requestId, string memoryKey, string? fallback)
{
    var text = (await brain.Get<IText>(memoryKey + "/intents/" + requestId.ToString("N")).Read()).Markdown;
    switch (PendingStore.Classify(text))
    {
        case MemoryAnswer.Ok:
            var intent = PendingStore.Find((await intents.Read()).Intents, text, requestId);
            await Act(requestId, intent?.Brief, intent?.State == IntentState.Confirmed, intent?.Id, null);
            return true;
        case MemoryAnswer.Failed:
            await Act(requestId, null, false, null, "Memory could not intend; answering without a brief");
            return true;
        default:
            if (fallback is null) { return false; }
            await Act(requestId, null, false, null, fallback);
            return true;
    }
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
        if (!PendingStore.KeepsClaim(outcome) && !await PendingStore.Release(brain, requestId))
        { await Ino.Status(brain, "Could not start the answer after " + PendingStore.MaxAttempts + " attempts"); }
    }
    finally { gate.Release(); }
}

// Every two seconds: a pending request is watched, one Memory has not answered within IntendGate is answered
// from whatever Memory has written (without a brief when nothing), and a released one is tried again once its
// backoff has passed. Entries older than an hour are deleted by PendingStore.Entries.
async Task Scan()
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), brain.Stopping);
            var entries = await PendingStore.Entries(brain);
            var now = DateTimeOffset.UtcNow;
            var gateTime = Ino.IntendGate(await app.Read());
            foreach (var (requestId, pending) in entries)
            {
                switch (PendingStore.Decide(pending, now, gateTime))
                {
                    case PendingStep.Watch:
                        await EnsureWatch(requestId);
                        break;
                    case PendingStep.Answer:
                        if (watches.TryRemove(requestId, out var watch)) { try { await watch.CancelAsync(); } catch (ObjectDisposedException) { } }
                        if (await Ino.MemoryKey(brain) is { } memoryKey)
                        {
                            await Answer(requestId, memoryKey, pending.Attempts > 0
                                ? "Trying the answer again without a brief" : "Memory was slow to intend; answering without a brief");
                        }
                        else { await Act(requestId, null, false, null, "Memory was slow to intend; answering without a brief"); }
                        break;
                }
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
