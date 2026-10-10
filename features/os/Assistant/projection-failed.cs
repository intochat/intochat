using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Client;

// A turn failed on the selected conversation: re-project, and ask for attention while the last run is the failed one.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await brain.React<TurnFailed>(Assistant.Conversation(brain), async _ =>
{
    if (Assistant.Settled(await Assistant.Project(brain)) == AgentRunStatus.Failed) { await app.Progress("attention", "The turn failed"); }
}, brain.Stopping);
