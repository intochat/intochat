using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var request in brain.On(brain.Get<IApp>(Room.Key(brain)), "publication", brain.Stopping))
{
    try { await Room.ShowPublication(brain, PublicationUi.Decode<AppPublicationRef>(request.Input)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Room.Status(brain, error.Message); }
}
