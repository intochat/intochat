using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var request in brain.On(brain.Get<IApp>(Ino.Key(brain)), "voice", brain.Stopping))
{
    await brain.Get<IApp>(Ino.AssistantKey(brain)).Request("voice", request.Input);
}
