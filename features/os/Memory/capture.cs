using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Time.Reminders;

// "capture" turns a finished turn into an episode: a short third-person note and an importance, kept in the
// episodes table under "agent#run". It runs in Memory after the turn completed, so it never blocks the turn.
// Memory's own capture agent (prompt capture.md, CaptureModel setting, no tools) writes the note; when it fails,
// is slow (30 seconds) or answers something that is not the JSON, a bounded fallback of the turn's own text is
// stored, marked fallback. A run whose episode already exists is skipped, so a redelivery never writes twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var episodes = brain.Get<ITable>(Episodes.TableKey(appKey));
var agent = brain.Get<IAgent>(Episodes.AgentKey(appKey));
var ceiling = TimeSpan.FromSeconds(30);
var defined = false;

await foreach (var request in brain.On(app, "capture", brain.Stopping))
{
    try
    {
        var ask = Episodes.ParseAsk(request.Input);
        if (!defined) { await episodes.Define(Episodes.Definition); defined = true; }
        var id = Episodes.Id(ask.Agent, ask.RunId);
        if (await episodes.Read(Key(id)) is not null) { continue; }
        var run = (await brain.Get<IAgent>(ask.Agent).Read()).Runs.FirstOrDefault(candidate => candidate.RunId == ask.RunId);
        if (run is null || run.Status != AgentRunStatus.Completed)
        {
            await brain.Get<IText>(appKey + "/status").Set(Clip("capture skipped: run " + ask.RunId + " "
                + (run is null ? "not found on " : "was not completed on ") + ask.Agent));
            continue;
        }
        var note = await Summarise(id, run);
        await episodes.Upsert(Key(id),
        [
            new("agent", JsonSerializer.Serialize(ask.Agent)), new("run", JsonSerializer.Serialize(ask.RunId)),
            new("at", JsonSerializer.Serialize((run.EndedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"))),
            new("summary", JsonSerializer.Serialize(note.Summary)), new("importance", note.Importance.ToString()),
            new("fallback", note.IsFallback ? "true" : "false"),
            // Upsert takes every non-key column: a new episode is neither consolidated nor claimed.
            new("consolidated_at", "null"), new("claim", "null"), new("claim_until", "null"),
        ]);
        await Recall.Index(brain, "episode:" + id, "episode", note.Summary, run.EndedAt ?? DateTimeOffset.UtcNow, note.Importance);
        await Quiet();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        // Best effort: the next completed turn is captured on its own.
        try { await brain.Get<IText>(appKey + "/status").Set(Clip("capture failed: " + error.Message)); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}

// Every new episode restarts the quiet-time reminder; consolidation runs once the brain has been quiet for QuietMinutes.
async Task Quiet()
{
    try
    {
        var quiet = Consolidation.Quiet((await app.Read()).Settings.GetValueOrDefault("QuietMinutes", "10"));
        await brain.Get<IReminder>(Consolidation.ReminderKey(appKey)).Start(quiet, TimeSpan.FromDays(365));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        try { await brain.Get<IText>(appKey + "/status").Set(Clip("capture could not arm consolidation: " + error.Message)); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}

static string Clip(string text) => text.Length <= 200 ? text : text[..200];

static TableValue[] Key(string id) => [new("id", JsonSerializer.Serialize(id))];

async Task<Episodes.Note> Summarise(string id, AgentRun run)
{
    var fallback = Episodes.Fallback(run.UserText, run.Reply);
    try
    {
        var snapshot = await app.Read();
        var model = snapshot.Settings.GetValueOrDefault("CaptureModel", "");
        var turn = JsonSerializer.Serialize(new
        {
            user = run.UserText,
            tools = run.ToolCalls.Select(call => new { call.Name, call.Arguments, call.Result, call.Error }),
            reply = run.Reply,
        });
        var text = await Ask(id, turn, model, snapshot.Files.GetValueOrDefault("prompts/capture.md", ""));
        return Episodes.ParseSummary(text) is { } parsed ? new(parsed.Summary, parsed.Importance, false) : fallback;
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return fallback; }
}

async Task<string> Ask(string runId, string user, string model, string prompt)
{
    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    waiting.CancelAfter(ceiling);
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var failedLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // Both subscriptions are live before the turn starts, so a fast signal is never missed.
    var watchers = new[]
    {
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnFailed>(agent, () => { failedLive.TrySetResult(); return Task.CompletedTask; }, waiting.Token))
            { if (turn.RunId == runId) { done.TrySetException(new InvalidOperationException("the agent failed: " + turn.Error)); return; } }
        }),
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnCompleted>(agent, async () =>
            {
                try
                {
                    await failedLive.Task.WaitAsync(waiting.Token);
                    // The agent is shared by every episode and keeps its transcript: clear it so each episode is
                    // summarised in isolation and an earlier turn never leaks into a later note.
                    await agent.Clear();
                    await agent.Start(AgentTurn.Text(user, runId: runId, model: model.Length > 0 ? model : null,
                        instructions: prompt, contracts: []));
                }
                catch (Exception error) when (error is not OperationCanceledException) { done.TrySetException(error); }
            }, waiting.Token))
            { if (turn.RunId == runId) { done.TrySetResult(turn.Reply); return; } }
        }),
    };
    try { return await done.Task.WaitAsync(ceiling, brain.Stopping); }
    catch (TimeoutException)
    {
        try { await agent.CancelRun(runId); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        throw;
    }
    finally
    {
        waiting.Cancel();
        try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
    }
}
