using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await Usage.Compose(brain);
await foreach (var request in brain.On<AppRequested>(brain.Get<IApp>(Usage.Key(brain)), brain.Stopping))
{
    if (request.Operation is "open" or "refresh") { await Usage.Compose(brain); }
}
