using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// A turn progressed on the selected conversation: re-project.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await brain.React<TurnProgressed>(Assistant.Conversation(brain), _ => Assistant.Project(brain), brain.Stopping);
