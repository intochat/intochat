using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// Signals invalidate a projection; their payloads never patch a second conversation history.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var pointer = brain.Get<IText>(appKey + "/conversation");
var chat = brain.Get<IChat>(appKey + "/transcript");
using var writer = new SemaphoreSlim(1, 1);
// Disposable delivery caches only. Every behavior start rebuilds these from authoritative state.
(string Source, long Generation, long Version)? projected = null;
(string Source, long Generation)? configured = null;

while (!brain.Stopping.IsCancellationRequested)
{
    var selected = await pointer.Read();
    var source = appKey + "/conversations/" + (selected.Markdown.Length == 0 ? "main" : selected.Markdown);
    var agent = brain.Get<IAgent>(source);
    using var scope = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    try
    {
        await Task.WhenAll(Selection(selected.Version, scope), Started(agent, source, selected.Version, scope.Token),
            Progress(agent, source, selected.Version, scope.Token), Completed(agent, source, selected.Version, scope.Token),
            Failed(agent, source, selected.Version, scope.Token));
    }
    catch (OperationCanceledException) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task Selection(int generation, CancellationTokenSource scope)
{
    async Task Reconcile()
    {
        if ((await pointer.Read()).Version != generation) { await scope.CancelAsync(); return; }
    }
    await foreach (var _ in brain.On<TextChanged>(pointer, Reconcile, scope.Token)) { await Reconcile(); }
}

async Task Started(IAgent agent, string source, long generation, CancellationToken ct)
{
    await foreach (var signal in brain.On<TurnStarted>(agent, () => Project(agent, source, generation, ct), ct)) { await Project(agent, source, generation, ct, signal.Version); }
}

async Task Progress(IAgent agent, string source, long generation, CancellationToken ct)
{
    await foreach (var signal in brain.On<TurnProgressed>(agent, () => Project(agent, source, generation, ct), ct)) { await Project(agent, source, generation, ct, signal.Version); }
}

async Task Completed(IAgent agent, string source, long generation, CancellationToken ct)
{
    await foreach (var signal in brain.On<TurnCompleted>(agent, () => Project(agent, source, generation, ct), ct)) { await Project(agent, source, generation, ct, signal.Version); }
}

async Task Failed(IAgent agent, string source, long generation, CancellationToken ct)
{
    await foreach (var signal in brain.On<TurnFailed>(agent, () => Project(agent, source, generation, ct), ct)) { await Project(agent, source, generation, ct, signal.Version); }
}

async Task Project(IAgent agent, string source, long generation, CancellationToken ct, long signalVersion = 0)
{
    await writer.WaitAsync(ct);
    try
    {
        if (signalVersion > 0 && projected is { } previous && previous.Source == source
            && previous.Generation == generation && previous.Version >= signalVersion) { return; }
        var state = await agent.Read();
        var identity = (source, generation, state.Version);
        if (projected == identity) { return; }
        // The previous scope is cancelled and drained before the next generation starts.
        ct.ThrowIfCancellationRequested();
        if (configured != (source, generation))
        {
            await brain.Get<IComposer>(appKey + "/composer").Configure(source, generation);
            configured = (source, generation);
        }
        var runs = state.Runs.ToList();
        if (state.ActiveRun is { } active)
        {
            runs.RemoveAll(run => run.RunId == active.RunId);
            runs.Add(active);
        }
        await chat.Project(new(source, generation, state.Version, [.. runs.SelectMany(run => new[]
        {
            new ChatEntry(run.RunId + "/user", ChatRole.User, run.UserText),
            new ChatEntry(run.RunId + "/assistant", ChatRole.Assistant, run.Reply ?? "")
            {
                Status = run.Status.ToString().ToLowerInvariant(), Error = run.Error,
                Activities = [.. run.ToolCalls.Select(call => new ChatActivity(call.CallId, call.Name, call.Arguments, call.Result, call.Error))],
            },
        })])
        {
            Actions = state.ActiveRun is { } running
                ? [new("stop/" + running.RunId, "Stop", JsonSerializer.Serialize(new { runId = running.RunId }))]
                : [],
        });
        projected = identity;
    }
    finally { writer.Release(); }
}
