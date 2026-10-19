using System.Globalization;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;

// "intend" rephrases a message into a brief and records it as an intent. Memory's own agent is told to
// rephrase, never to answer, given the message, the active intents and the working memory; its reply is
// declared on the intents neuron, rephrased, and confirmed when its confidence reaches the ConfirmGate
// setting. The answer under the request id is "ok <intent id>" or "failed: <reason>"; the intent id matters
// because a continued intent keeps the request id it was first heard under. Each write reads the revision afresh
// and is retried on a revision conflict (Intents.ShouldRetry); a failure declares nothing, and
// a request whose answer is already written is skipped, so a redelivery never declares twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
var agent = brain.Get<IAgent>(Intents.AgentKey(appKey));
var gateWarned = false;
var ceiling = TimeSpan.FromSeconds(30);

await foreach (var request in brain.On(app, "intend", brain.Stopping))
{
    var answered = brain.Get<IText>(Intents.AnswerKey(appKey, request.RequestId.ToString("D")));
    string answer;
    try
    {
        var ask = Intents.ParseAsk(request.Input);
        answered = brain.Get<IText>(Intents.AnswerKey(appKey, ask.RequestId));
        if ((await answered.Read()).Markdown.Length > 0) { continue; }
        answer = Intents.Ok(await Intend(ask));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { answer = "failed: " + error.Message; }
    await answered.Set(answer);
}

async Task<Guid> Intend(Intents.Ask ask)
{
    var snapshot = await app.Read();
    var model = snapshot.Settings.GetValueOrDefault("Model", "");
    var gate = 0.8;
    if (snapshot.Settings.TryGetValue("ConfirmGate", out var gateText)
        && (!double.TryParse(gateText, CultureInfo.InvariantCulture, out gate) || !double.IsFinite(gate) || gate < 0 || gate > 1))
    {
        gate = 0.8;
        if (!gateWarned)
        {
            gateWarned = true;
            // Best effort: a warning that cannot be written must not fail the request.
            try { await brain.Get<IText>(appKey + "/status").Set("ConfirmGate is not a number in 0..1; using 0.8"); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        }
    }
    var active = Intents.RenderActive((await intents.Read()).Intents, 4000);
    var working = (await brain.Get<IText>(appKey + "/working").Read()).Markdown;
    var user = JsonSerializer.Serialize(new { message = ask.Message, activeIntents = active, workingMemory = working });

    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    waiting.CancelAfter(ceiling);
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Ordering: the TurnFailed watcher is live first (its onSubscribed releases failedLive); the TurnCompleted
    // watcher waits for that in its own onSubscribed, once its subscription is live, and only then starts the
    // turn. So both subscriptions exist before the turn can publish, and a fast signal is never missed.
    var failedLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var watchers = new[]
    {
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnFailed>(agent, () => { failedLive.TrySetResult(); return Task.CompletedTask; }, waiting.Token))
            { if (turn.RunId == ask.RequestId) { done.TrySetException(new InvalidOperationException("the agent failed: " + turn.Error)); return; } }
        }),
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnCompleted>(agent, async () =>
            {
                try
                {
                    await failedLive.Task.WaitAsync(waiting.Token);
                    await agent.Start(AgentTurn.Text(user, runId: ask.RequestId, model: model.Length > 0 ? model : null,
                        instructions: snapshot.Files.GetValueOrDefault("prompts/intend.md", ""), contracts: []));
                }
                catch (Exception error) when (error is not OperationCanceledException) { done.TrySetException(error); }
            }, waiting.Token))
            { if (turn.RunId == ask.RequestId) { done.TrySetResult(turn.Reply); return; } }
        }),
    };
    string text;
    try { text = await done.Task.WaitAsync(ceiling, brain.Stopping); }
    catch (TimeoutException)
    {
        // The turn would keep Memory's agent busy and the next intend would find it so: cancel it first.
        try { await agent.CancelRun(ask.RequestId); }
        catch (Exception cancel) when (!brain.Stopping.IsCancellationRequested)
        { throw new TimeoutException("the agent did not answer in 30 seconds; cancelling its turn also failed: " + cancel.Message); }
        throw new TimeoutException("the agent did not answer in 30 seconds");
    }
    finally
    {
        waiting.Cancel();
        try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
    }

    var reply = Intents.ParseReply(text) ?? throw new InvalidOperationException("the agent's reply was not the expected JSON");
    var id = await intents.Declare(new(ask.RequestId, ask.Message, ask.Conversation, reply.Continues));
    try
    {
        await Retrying(async () => await intents.Rephrase(id, reply.Brief, reply.Confidence, (await intents.Read()).Revision));
        await Retrying(async () =>
        {
            var current = await intents.Read();
            if (Intents.ShouldConfirm(current.Intents.FirstOrDefault(intent => intent.Id == id), reply.Confidence, gate))
            { await intents.Transition(id, IntentState.Confirmed, "confident", current.Revision); }
        });
        return id;
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        // Compensate: an intent this request created and left in Heard is abandoned; a continued one is untouched.
        var reason = "intend failed: " + error.Message;
        try
        {
            var current = await intents.Read();
            if (Intents.ShouldCompensate(current.Intents.FirstOrDefault(intent => intent.Id == id), ask.RequestId))
            { await intents.Transition(id, IntentState.Abandoned, reason, current.Revision); }
        }
        catch (Exception compensation) when (!brain.Stopping.IsCancellationRequested)
        { throw new InvalidOperationException(error.Message + "; abandoning the intent also failed: " + compensation.Message, error); }
        throw;
    }
}

// A write that meets a revision conflict is read again and retried, up to Intents.FireAttempts in all.
async Task Retrying(Func<Task> write)
{
    for (var attempt = 1; ; attempt++)
    {
        try { await write(); return; }
        catch (Exception error) when (Intents.ShouldRetry(error, attempt) && !brain.Stopping.IsCancellationRequested) { }
    }
}
