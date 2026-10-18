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
// setting. The answer under the request id is "ok" or "failed: <reason>"; a failure declares nothing, and
// a request whose answer is already written is skipped, so a redelivery never declares twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
var agent = brain.Get<IAgent>(Intents.AgentKey(appKey));
var ceiling = TimeSpan.FromSeconds(30);

await foreach (var request in brain.On(app, "intend", brain.Stopping))
{
    var answered = brain.Get<IText>(Intents.AnswerKey(appKey, request.RequestId.ToString("N")));
    string answer;
    try
    {
        var ask = Intents.ParseAsk(request.Input);
        answered = brain.Get<IText>(Intents.AnswerKey(appKey, ask.RequestId));
        if ((await answered.Read()).Markdown.Length > 0) { continue; }
        await Intend(ask);
        answer = "ok";
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { answer = "failed: " + error.Message; }
    await answered.Set(answer);
}

async Task Intend(Intents.Ask ask)
{
    var snapshot = await app.Read();
    var model = snapshot.Settings.GetValueOrDefault("Model", "");
    var gate = double.TryParse(snapshot.Settings.GetValueOrDefault("ConfirmGate", "0.8"), CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.8;
    var active = Intents.RenderActive((await intents.Read()).Intents, 4000);
    var working = (await brain.Get<IText>(appKey + "/working").Read()).Markdown;
    var user = JsonSerializer.Serialize(new { message = ask.Message, activeIntents = active, workingMemory = working });

    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    waiting.CancelAfter(ceiling);
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var watchers = new[]
    {
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnCompleted>(agent, waiting.Token))
            { if (turn.RunId == ask.RequestId) { done.TrySetResult(turn.Reply); return; } }
        }),
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnFailed>(agent, waiting.Token))
            { if (turn.RunId == ask.RequestId) { done.TrySetException(new InvalidOperationException("the agent failed: " + turn.Error)); return; } }
        }),
    };
    string text;
    try
    {
        await agent.Start(AgentTurn.Text(user, runId: ask.RequestId, model: model.Length > 0 ? model : null,
            instructions: snapshot.Files.GetValueOrDefault("prompts/intend.md", ""), contracts: []));
        text = await done.Task.WaitAsync(ceiling, brain.Stopping);
    }
    catch (TimeoutException) { throw new TimeoutException("the agent did not answer in 30 seconds"); }
    finally
    {
        waiting.Cancel();
        try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
    }

    var reply = Intents.ParseReply(text) ?? throw new InvalidOperationException("the agent's reply was not the expected JSON");
    var id = await intents.Declare(new(ask.RequestId, ask.Message, ask.Conversation, reply.Continues));
    var revision = (await intents.Read()).Revision;
    await intents.Rephrase(id, reply.Brief, reply.Confidence, revision);
    if (reply.Confidence >= gate) { await intents.Transition(id, IntentState.Confirmed, "confident", revision + 1); }
}
