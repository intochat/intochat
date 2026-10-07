#:project /brain/src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/AI/DigitalBrain.Modules.AI.Contracts/DigitalBrain.Modules.AI.Contracts.csproj
using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// The conversation. The IAgent conversation is the transcript's source of truth; every card is
// derived from it and from progress signals keyed by (run, sequence), so redelivery and a fresh
// run after reaping rewrite the same cards instead of duplicating them. The conversation is the
// signals' publisher, so the signal loops follow the current one and resubscribe when "new" swaps it.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;
var conversationChanged = new CancellationTokenSource();
string[] Tools = ["discover_capabilities", "select_capability", "list_neurons", "list_active_neurons", "show_form", "show_view"];

await Task.WhenAll(Operations(), SendClicks(), NewClicks(), FollowConversation());
return;

async Task<string> ConversationId()
{
    var current = (await brain.Get<IText>(Name("conversation")).Read()).Markdown;
    if (current.Length > 0) { return current; }
    await brain.Get<IText>(Name("conversation")).Set("main");
    return "main";
}

async Task<IAgent> Conversation() => brain.Get<IAgent>(Name("conversations/" + await ConversationId()));

async Task<string> Send(string message)
{
    var snapshot = await app.Read();
    // The setting is a catalog choice id; the catalog refuses one that is unknown or unavailable.
    var choice = snapshot.Settings.GetValueOrDefault("Model", "");
    var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
    var turn = AgentTurn.Text(message,
        instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", ""),
        tools: Tools) with { Model = model };
    var runId = await (await Conversation()).Start(turn);
    await brain.Get<ICard>(Name($"turn-{runId}-user")).Set("You", message);
    await brain.Get<ICard>(Name($"turn-{runId}-answer")).Set("Assistant", "…");
    await RenderAsync();
    return runId;
}

async Task Operations()
{
    await foreach (var invoked in app.Invocations(brain, brain.Stopping))
    {
        switch (invoked.Operation)
        {
            case "send" when invoked.Input.Length is >= 1 and <= 32000:
                // A second message while a turn is running is refused by the conversation.
                try { await app.Respond(new AppResponse(invoked.InvocationId, await Send(invoked.Input), null)); }
                catch (Exception error) { await app.Respond(new AppResponse(invoked.InvocationId, null, error.Message)); }
                break;
            case "send":
                await app.Respond(new AppResponse(invoked.InvocationId, null, "A message has 1-32000 characters."));
                break;
            case "transcript":
                await app.Respond(new AppResponse(invoked.InvocationId, await TranscriptJson(), null));
                break;
            case "new":
                var id = "c-" + Guid.NewGuid().ToString("N")[..12];
                await brain.Get<IText>(Name("conversation")).Set(id);
                var previous = conversationChanged;
                conversationChanged = new CancellationTokenSource();
                await previous.CancelAsync();
                await RenderAsync();
                await app.Respond(new AppResponse(invoked.InvocationId, id, null));
                break;
        }
    }
}

async Task SendClicks()
{
    await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(Name("send")), brain.Stopping))
    {
        var draft = brain.Get<ITextField>(Name("draft"));
        var message = (await draft.Read()).Value.Trim();
        if (message.Length is < 1 or > 32000) { continue; }
        // A refusal (a turn still running) keeps the message in the draft and says why; the button
        // keeps working either way.
        try
        {
            await Send(message);
            await draft.SetValue("");
        }
        catch (Exception error) { await brain.Get<IText>(Name("status")).Set(error.Message); }
    }
}

async Task NewClicks()
{
    await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(Name("new")), brain.Stopping))
    { await app.Invoke(new(Guid.NewGuid(), "new", "")); }
}

// The token is taken before the conversation is read: a swap in between cancels it, so the loop
// never keeps following the conversation it replaced.
async Task FollowConversation()
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        var changed = conversationChanged.Token;
        var conversation = await Conversation();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping, changed);
        try { await Task.WhenAll(Progress(conversation, scope.Token), Completions(conversation, scope.Token), Failures(conversation, scope.Token)); }
        catch (OperationCanceledException) when (!brain.Stopping.IsCancellationRequested) { }
    }
}

async Task Progress(IAgent conversation, CancellationToken token)
{
    await foreach (var step in brain.On<TurnProgressed>(conversation, token))
    {
        // Text arrives piece by piece; the answer is the conversation's, set when the turn settles,
        // so a piece is never stored as a card of its own.
        if (step.Kind == "text") { await brain.Get<IText>(Name("status")).Set("Working…"); continue; }
        var stepKey = Name($"turn-{step.RunId}-step-{step.Sequence}");
        var (title, body) = step.Kind switch
        {
            "text" => ("Answer", step.Text ?? ""),
            "tool-call" => ("Tool: " + step.Tool, step.Payload ?? ""),
            "tool-result" => ("Result: " + step.Tool, step.Text ?? step.Payload ?? ""),
            "card" => ("Card: " + step.Tool, step.Payload ?? ""),
            _ => ("Usage", step.Payload ?? ""),
        };
        await brain.Get<ICard>(stepKey).Set(title, body);
        var answer = brain.Get<ICard>(Name($"turn-{step.RunId}-answer"));
        var current = await answer.Read();
        if (step.Kind != "text" && current.Children.All(child => child.Name != stepKey))
        { await answer.Set(current.Title, current.Body, [.. current.Children, new UiChildRef(UIVocabulary.CardType, stepKey)]); }
        await brain.Get<IText>(Name("status")).Set("Working…");
    }
}

async Task Completions(IAgent conversation, CancellationToken token)
{
    await foreach (var done in brain.On<TurnCompleted>(conversation, token))
    { await SettleAsync(done.RunId); }
}

async Task Failures(IAgent conversation, CancellationToken token)
{
    await foreach (var failed in brain.On<TurnFailed>(conversation, token))
    { await SettleAsync(failed.RunId); }
}

// The finished turn's answer is whatever the conversation kept: the reply or the failure text.
async Task SettleAsync(string runId)
{
    var run = (await (await Conversation()).Read()).Runs.FirstOrDefault(item => item.RunId == runId);
    if (run is null) { return; }
    var answer = brain.Get<ICard>(Name($"turn-{runId}-answer"));
    await answer.Set("Assistant", Answer(run), (await answer.Read()).Children);
    await brain.Get<IText>(Name("status")).Set("");
    await RenderAsync();
}

// The transcript layout is re-derived wholesale from the conversation, never patched.
async Task RenderAsync()
{
    var state = await (await Conversation()).Read();
    var runs = state.Runs.Select(run => run.RunId).ToList();
    if (state.ActiveRun is { } active && !runs.Contains(active.RunId)) { runs.Add(active.RunId); }
    var transcript = brain.Get<ILayout>(Name("transcript"));
    await transcript.Set(new LayoutDefinition("column",
        [.. runs.SelectMany(run => new[]
        {
            new UiChildRef(UIVocabulary.CardType, Name($"turn-{run}-user")),
            new UiChildRef(UIVocabulary.CardType, Name($"turn-{run}-answer")),
        })], FollowEnd: true), (await transcript.Read()).Revision);
}

async Task<string> TranscriptJson()
{
    var state = await (await Conversation()).Read();
    var runs = state.Runs.ToList();
    if (state.ActiveRun is { } active && runs.All(run => run.RunId != active.RunId)) { runs.Add(active); }
    var items = new List<object>();
    foreach (var run in runs)
    {
        var runId = run.RunId;
        var steps = new List<object>();
        foreach (var child in (await brain.Get<ICard>(Name($"turn-{runId}-answer")).Read()).Children)
        {
            var card = await brain.Get<ICard>(child.Name).Read();
            var kind = card.Title.StartsWith("Tool: ", StringComparison.Ordinal) ? "tool-call"
                : card.Title.StartsWith("Result: ", StringComparison.Ordinal) ? "tool-result"
                : card.Title.StartsWith("Card: ", StringComparison.Ordinal) ? "card" : "receipt";
            steps.Add(new { kind, tool = card.Title.Contains(": ", StringComparison.Ordinal) ? card.Title[(card.Title.IndexOf(": ", StringComparison.Ordinal) + 2)..] : null, text = card.Body });
        }
        var status = run.Status switch { AgentRunStatus.Running => "running", AgentRunStatus.Completed => "done", _ => "failed" };
        items.Add(new { runId, user = run.UserText, assistant = run.Status == AgentRunStatus.Running ? "" : Answer(run), steps, status });
    }
    return JsonSerializer.Serialize(items);
}

// A run that did not complete answers with why: its error, or how it ended.
static string Answer(AgentRun run) => run.Reply ?? "The turn failed: " + (run.Error ?? run.Status.ToString().ToLowerInvariant());
