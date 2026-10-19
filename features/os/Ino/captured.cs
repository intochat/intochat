using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// After a turn completes, Memory is asked to capture it as an episode ("memory.capture" with the conversation
// agent and the run id). The conversation agent's own TurnCompleted signal names both, so nothing is read back
// from the chat. The watched agent is the chat's selected conversation: a side loop follows it, and when the
// selection changes (ChatSelectionChanged, or the selection read when the subscription goes live) the loop for
// the old agent is cancelled and one for the new agent starts. Nothing is asked when Memory is not installed.
// A run is asked once per session; Memory also skips a run whose episode exists, so a replay never captures twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var chat = brain.Get<IChat>(Ino.DataKey(brain) + "/transcript");
var asked = new HashSet<string>();
var gate = new object();
string? watched = null;
CancellationTokenSource? watching = null;
Task side = Task.CompletedTask;

await foreach (var change in brain.On<ChatSelectionChanged>(chat, async () => Follow((await Ino.EnsureSelection(brain)).Agent), brain.Stopping))
{
    Follow(change.Selection.Agent);
}
CancellationTokenSource? last;
lock (gate) { last = watching; }
last?.Cancel();
try { await side; } catch (OperationCanceledException) { }

void Follow(string agentKey)
{
    lock (gate)
    {
        if (agentKey == watched) { return; }
        watching?.Cancel();
        var previous = side;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
        watching = stop;
        watched = agentKey;
        side = Task.Run(async () =>
        {
            try { await previous; } catch (Exception) { }
            await Watch(agentKey, stop.Token);
        });
    }
}

async Task Watch(string agentKey, CancellationToken ct)
{
    try
    {
        await foreach (var turn in brain.On<TurnCompleted>(brain.Get<IAgent>(agentKey), ct))
        {
            try
            {
                bool first;
                lock (asked) { first = asked.Add(turn.AgentId + "#" + turn.RunId); }
                if (!first || await Ino.MemoryKey(brain) is not { } memoryKey) { continue; }
                await brain.Get<IApp>(memoryKey).Request("capture",
                    JsonSerializer.Serialize(new { agent = turn.AgentId, runId = turn.RunId }));
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            { await Ino.Status(brain, "Could not ask Memory to capture the turn: " + Ino.Clip(error.Message)); }
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        try { await Ino.Status(brain, "Could not follow the conversation for capture: " + Ino.Clip(error.Message)); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        lock (gate) { if (watched == agentKey) { watched = null; } }
    }
}
