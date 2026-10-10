using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Client;

// A turn started on the selected conversation: re-project, and show it working while a run is active.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await brain.React<TurnStarted>(Assistant.Conversation(brain), async _ =>
{
    if ((await Assistant.Project(brain)).ActiveRun is not null) { await app.Progress("working", "Working on your message"); }
}, brain.Stopping);
