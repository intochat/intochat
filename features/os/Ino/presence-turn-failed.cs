using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The assistant's turn failed: Ino shows the error, then rests.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var failed in brain.On<TurnFailed>(Ino.Conversation(brain), brain.Stopping))
{
    await Ino.Fire(brain, "fail");
    await Task.Delay(TimeSpan.FromSeconds(3), brain.Stopping);
    await Ino.RestFrom(brain, "error");
}
