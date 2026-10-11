using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The assistant's selected conversation started a turn: Ino is listening.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var started in brain.On<TurnStarted>(Ino.Conversation(brain), brain.Stopping)) { await Ino.Fire(brain, "listen"); }
