using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The assistant's turn completed: Ino speaks, then rests.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var completed in brain.On<TurnCompleted>(Ino.Conversation(brain), brain.Stopping))
{
    await Ino.Fire(brain, "speak");
    await Task.Delay(TimeSpan.FromSeconds(2), brain.Stopping);
    await Ino.RestFrom(brain, "speaking");
}
