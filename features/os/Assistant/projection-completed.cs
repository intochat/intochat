using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Client;

// A turn completed on the selected conversation: re-project, and report it answered while the last run is the completed one.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await brain.React<TurnCompleted>(Assistant.Conversation(brain), async _ =>
{
    if (Assistant.Settled(await Assistant.Project(brain)) == AgentRunStatus.Completed) { await app.Progress("completed", "Answered"); }
}, brain.Stopping);
