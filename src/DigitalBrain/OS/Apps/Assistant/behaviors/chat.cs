#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/Modules/AI/DigitalBrain.Modules.AI.Contracts/DigitalBrain.Modules.AI.Contracts.csproj
using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
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
// run after reaping rewrite the same cards instead of duplicating them.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;
var execution = brain.Get<IAgentExecution>(Name("execution"));
string[] Tools = ["discover_capabilities", "select_capability", "list_neurons", "list_active_neurons", "show_form", "show_view"];

await Task.WhenAll(Operations(), SendClicks(), NewClicks(), Progress(), Completions(), Failures());
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
    var runId = await execution.Start(new(Name("conversations/" + await ConversationId()),
        snapshot.Settings.GetValueOrDefault("Model", ""), snapshot.Files.GetValueOrDefault("prompts/system.md", ""), Tools, message));
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
        await draft.SetValue("");
        await Send(message);
    }
}

async Task NewClicks()
{
    await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(Name("new")), brain.Stopping))
    { await app.Invoke(new(Guid.NewGuid(), "new", "")); }
}

async Task Progress()
{
    await foreach (var step in brain.On<AgentTurnProgressed>(execution, brain.Stopping))
    {
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

async Task Completions()
{
    await foreach (var done in brain.On<AgentTurnCompleted>(execution, brain.Stopping))
    { await SettleAsync(done.RunId); }
}

async Task Failures()
{
    await foreach (var failed in brain.On<AgentTurnFailed>(execution, brain.Stopping))
    { await SettleAsync(failed.RunId); }
}

// The finished turn's answer is whatever the conversation kept: the reply or the failure text.
async Task SettleAsync(string runId)
{
    var turn = (await (await Conversation()).ReadConversation()).Turns.FirstOrDefault(item => item.RunId == runId);
    if (turn is null) { return; }
    var answer = brain.Get<ICard>(Name($"turn-{runId}-answer"));
    await answer.Set("Assistant", turn.AssistantText, (await answer.Read()).Children);
    await brain.Get<IText>(Name("status")).Set("");
    await RenderAsync();
}

// The transcript layout is re-derived wholesale from the conversation, never patched.
async Task RenderAsync()
{
    var state = await (await Conversation()).ReadConversation();
    var runs = state.Turns.Select(turn => turn.RunId).ToList();
    if (state.ActiveRunId is { } active && !runs.Contains(active)) { runs.Add(active); }
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
    var state = await (await Conversation()).ReadConversation();
    var runs = state.Turns.Select(turn => (turn.RunId, turn.UserText, (string?)turn.AssistantText)).ToList();
    if (state.ActiveRunId is { } active && runs.All(run => run.RunId != active))
    { runs.Add((active, (await brain.Get<ICard>(Name($"turn-{active}-user")).Read()).Body, null)); }
    var items = new List<object>();
    foreach (var (runId, user, assistant) in runs)
    {
        var steps = new List<object>();
        foreach (var child in (await brain.Get<ICard>(Name($"turn-{runId}-answer")).Read()).Children)
        {
            var card = await brain.Get<ICard>(child.Name).Read();
            var kind = card.Title.StartsWith("Tool: ", StringComparison.Ordinal) ? "tool-call"
                : card.Title.StartsWith("Result: ", StringComparison.Ordinal) ? "tool-result"
                : card.Title.StartsWith("Card: ", StringComparison.Ordinal) ? "card" : "receipt";
            steps.Add(new { kind, tool = card.Title.Contains(": ", StringComparison.Ordinal) ? card.Title[(card.Title.IndexOf(": ", StringComparison.Ordinal) + 2)..] : null, text = card.Body });
        }
        var status = assistant is null ? "running" : assistant.StartsWith("The turn failed:", StringComparison.Ordinal) ? "failed" : "done";
        items.Add(new { runId, user, assistant = assistant ?? "", steps, status });
    }
    return JsonSerializer.Serialize(items);
}
