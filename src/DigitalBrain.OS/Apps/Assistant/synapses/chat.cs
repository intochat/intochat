using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Integrations;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Postgres;

// Conversation commands. IAgent owns the transcript; projection.cs derives the chat UI.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;
// What the model may act on: the contracts this app composes, checked by the compiler.
string[] Contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!, typeof(IAppIndex).FullName!,
    typeof(IWorkspace).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!];

await ConversationId();
await Task.WhenAll(Operations(), NewClicks());
return;

async Task<string> ConversationId()
{
    var current = (await brain.Get<IText>(Name("conversation")).Read()).Markdown;
    if (current.Length > 0) { return current; }
    await brain.Get<IText>(Name("conversation")).Set("main");
    return "main";
}

async Task<IAgent> Conversation() => brain.Get<IAgent>(Name("conversations/" + await ConversationId()));

async Task<string> Send(string message, string runId)
{
    var snapshot = await app.Read();
    // The setting is a catalog choice id; the catalog refuses one that is unknown or unavailable.
    var choice = snapshot.Settings.GetValueOrDefault("Model", "");
    var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
    var turn = AgentTurn.Text(message,
        instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", ""),
        contracts: Contracts) with { Model = model, RunId = runId };
    return await (await Conversation()).Start(turn);
}

async Task Operations()
{
    await foreach (var invoked in app.Invocations(brain, brain.Stopping))
    {
        switch (invoked.Operation)
        {
            case "send" when invoked.Input.Length is >= 1 and <= 32000:
                // A second message while a turn is running is refused by the conversation.
                try { await app.Respond(new AppResponse(invoked.InvocationId, await Send(invoked.Input, invoked.InvocationId.ToString("N")), null)); }
                catch (Exception error) { await app.Respond(new AppResponse(invoked.InvocationId, null, error.Message)); }
                break;
            case "send":
                await app.Respond(new AppResponse(invoked.InvocationId, null, "A message has 1-32000 characters."));
                break;
            case "transcript":
                await app.Respond(new AppResponse(invoked.InvocationId, await TranscriptJson(), null));
                break;
            case "new":
                var id = "c-" + invoked.InvocationId.ToString("N");
                var current = brain.Get<IText>(Name("conversation"));
                if ((await current.Read()).Markdown != id) { await current.Set(id); }
                await brain.Get<IText>(Name("status")).Set("");
                await app.Respond(new AppResponse(invoked.InvocationId, id, null));
                break;
        }
    }
}

async Task NewClicks()
{
    await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Name("new")), brain.Stopping))
    {
        // The persisted click identity survives behavior restart and redelivery.
        if (clicked.RequestId != Guid.Empty) { await app.Invoke(new(clicked.RequestId, "new", "")); }
    }
}

// Responses come directly from the authoritative conversation, including partial text and tools.
async Task<string> TranscriptJson()
{
    var state = await (await Conversation()).Read();
    var runs = state.Runs.ToList();
    if (state.ActiveRun is { } active)
    {
        runs.RemoveAll(run => run.RunId == active.RunId);
        runs.Add(active);
    }
    return JsonSerializer.Serialize(runs.Select(run => new
    {
        runId = run.RunId, user = run.UserText, assistant = run.Reply ?? "", error = run.Error,
        status = run.Status switch { AgentRunStatus.Running => "running", AgentRunStatus.Completed => "done", _ => "failed" },
        steps = run.ToolCalls.Select(call => new { kind = "tool", tool = call.Name, input = call.Arguments, result = call.Result, error = call.Error }),
    }));
}
