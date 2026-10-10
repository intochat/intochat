using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The selected conversation was cleared: re-project.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await brain.React<TranscriptCleared>(Assistant.Conversation(brain), _ => Assistant.Project(brain), brain.Stopping);
