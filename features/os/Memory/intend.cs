using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Intents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Text;

// "intend" rephrases a message into a brief and records it as an intent. Memory's own agent is told to
// rephrase, never to answer, given the message, the active intents and the working memory; its reply is
// declared on the intents neuron, rephrased, and confirmed when its confidence reaches the ConfirmGate
// setting. The answer under the request id is "ok <intent id>" or "failed: <reason>"; the intent id matters
// because a continued intent keeps the request id it was first heard under. The model has a 30-second
// reply deadline; after it replies, rephrase and confirmation each allow at most three mutation attempts.
// Revision conflicts reread; transient edge failures read back before retrying, since a lost reply may
// hide a committed mutation. Declare is never retried here. A request already answered is skipped.
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
        await Retrying(id, intent => MatchesReply(intent, reply),
            current => intents.Rephrase(id, reply.Brief, reply.Confidence, current.Revision));
        await Retrying(id, intent =>
        {
            if (!MatchesReply(intent, reply)) { throw new InvalidOperationException("the intent changed before confirmation"); }
            return !Intents.ShouldConfirm(intent, reply.Confidence, gate);
        }, current => intents.Transition(id, IntentState.Confirmed, "confident", current.Revision));
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

// Keep this recovery local to intend. Other Memory operations have different idempotency rules.
// Three writes at most, with 250/500ms backoff for transient HTTP responses. Every retry uses a fresh
// revision; even the final failed response gets read back without issuing a fourth mutation.
async Task Retrying(Guid id, Func<Intent, bool> complete, Func<IntentsSnapshot, Task> write)
{
    Intent? before = null;
    bool Completed(IntentsSnapshot current)
    {
        var intent = current.Intents.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("the intent no longer exists");
        if (intent.State is IntentState.Done or IntentState.Abandoned)
        { throw new InvalidOperationException("the intent became terminal while intending"); }
        if (complete(intent)) { return true; }
        if (before is not null && (before.State != intent.State || before.Confidence != intent.Confidence
            || !SameBrief(before.Brief, intent.Brief)))
        { throw new InvalidOperationException("the intent changed while intending"); }
        before = intent;
        return false;
    }

    for (var attempt = 1; ; attempt++)
    {
        var current = await intents.Read();
        if (Completed(current)) { return; }
        try { await write(current); return; }
        catch (HttpRequestException error) when (IsTransient(error) && !brain.Stopping.IsCancellationRequested)
        {
            if (Completed(await intents.Read())) { return; }
            if (attempt >= Intents.FireAttempts) { throw; }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), brain.Stopping);
        }
        catch (Exception error) when (Intents.ShouldRetry(error, attempt) && !brain.Stopping.IsCancellationRequested) { }
    }
}

static bool IsTransient(HttpRequestException error)
    => error.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int?)error.StatusCode is >= 500 and <= 599;

static bool MatchesReply(Intent intent, Intents.Reply reply)
    => intent.State is IntentState.Rephrased or IntentState.Confirmed or IntentState.Dispatched or IntentState.Waiting
        && intent.Confidence == reply.Confidence && SameBrief(intent.Brief, reply.Brief);

static bool SameBrief(IntentBrief? left, IntentBrief? right)
    => left is null ? right is null : right is not null && left.Goal == right.Goal
        && left.Inputs.SequenceEqual(right.Inputs) && left.Constraints.SequenceEqual(right.Constraints)
        && left.Acceptance.SequenceEqual(right.Acceptance);
