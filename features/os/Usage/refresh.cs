using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Usage.Key(brain) + "/refresh"), brain.Stopping))
{
    await Usage.Compose(brain);
}
