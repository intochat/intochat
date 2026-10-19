using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// After a turn completes, Memory is asked to capture it as an episode ("memory.capture" with the conversation
// agent and the run id). The conversation agent's own TurnCompleted signal names both, so nothing is read back
// from the chat. The watched agent is the chat's selected conversation: a side loop follows it, and when the
// selection changes (ChatSelectionChanged, or the selection read when the subscription goes live) a loop for the new
// agent starts while the old agent stays watched until its active run completes (at most five minutes), so a turn in
// flight when the user switches is still captured. Every time a watch goes live (a new selection, a restart, a retry
// five seconds after a failed watch) it catches up: each Completed run of the agent not asked yet is asked now, and
// the released agent gets one last catch-up. Nothing is asked when Memory is not installed. A run is asked once per
// session (the last 256 are remembered); Memory also skips a run whose episode exists, so a replay or a catch-up
// never captures twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var chat = brain.Get<IChat>(Ino.DataKey(brain) + "/transcript");
var asked = new HashSet<string>(StringComparer.Ordinal);
var order = new Queue<string>();
const int Remembered = 256;
var linger = TimeSpan.FromMinutes(5);
var gate = new object();
string? watched = null;
CancellationTokenSource? watching = null;
var sides = new List<Task>();

await foreach (var change in brain.On<ChatSelectionChanged>(chat, async () => Follow((await Ino.EnsureSelection(brain)).Agent), brain.Stopping))
{
    Follow(change.Selection.Agent);
}
Task[] running;
lock (gate) { watching?.Cancel(); running = [.. sides]; }
try { await Task.WhenAll(running); } catch (OperationCanceledException) { }

void Follow(string agentKey)
{
    lock (gate)
    {
        if (agentKey == watched) { return; }
        var previousKey = watched;
        var previousStop = watching;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
        watching = stop;
        watched = agentKey;
        sides.RemoveAll(task => task.IsCompleted);
        sides.Add(Task.Run(() => Watch(agentKey, stop.Token)));
        if (previousKey is not null && previousStop is not null) { sides.Add(Task.Run(() => Release(previousKey, previousStop))); }
    }
}

// The previous conversation stays watched until its active run completes (or five minutes pass); then its watch is
// cancelled and a last catch-up asks for whatever completed meanwhile.
async Task Release(string agentKey, CancellationTokenSource stop)
{
    try
    {
        var until = DateTimeOffset.UtcNow + linger;
        var agent = brain.Get<IAgent>(agentKey);
        while (DateTimeOffset.UtcNow < until && (await agent.Read()).ActiveRun is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), brain.Stopping);
        }
    }
    catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Report("Could not wait for the previous conversation's turn: " + Ino.Clip(error.Message)); }
    await stop.CancelAsync();
    await CatchUp(agentKey, brain.Stopping);
}

// A failed watch is reported and retried after five seconds until its selection moves on.
async Task Watch(string agentKey, CancellationToken ct)
{
    var agent = brain.Get<IAgent>(agentKey);
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await foreach (var turn in brain.On<TurnCompleted>(agent, () => CatchUp(agentKey, ct), ct))
            {
                await Ask(turn.AgentId, turn.RunId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await Report("Could not follow the conversation for capture: " + Ino.Clip(error.Message) + "; retrying in 5 seconds."); }
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }
    }
}

// Every Completed run of the agent that was not asked yet (Memory skips one it already holds).
async Task CatchUp(string agentKey, CancellationToken ct)
{
    try
    {
        foreach (var run in (await brain.Get<IAgent>(agentKey).Read()).Runs.Where(run => run.Status == AgentRunStatus.Completed))
        {
            await Ask(agentKey, run.RunId, ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Report("Could not catch up on the conversation's turns: " + Ino.Clip(error.Message)); }
}

async Task Ask(string agentKey, string runId, CancellationToken ct)
{
    var key = agentKey + "#" + runId;
    lock (asked)
    {
        if (!asked.Add(key)) { return; }
        order.Enqueue(key);
        while (order.Count > Remembered) { asked.Remove(order.Dequeue()); }
    }
    try
    {
        if (await Ino.MemoryKey(brain) is not { } memoryKey) { return; }
        await brain.Get<IApp>(memoryKey).Request("capture", JsonSerializer.Serialize(new { agent = agentKey, runId }));
    }
    catch (Exception error) when (!ct.IsCancellationRequested)
    {
        // Not asked after all: a later catch-up may ask again.
        lock (asked) { asked.Remove(key); }
        await Report("Could not ask Memory to capture the turn: " + Ino.Clip(error.Message));
    }
}

async Task Report(string text)
{
    try { await Ino.Status(brain, text); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}
