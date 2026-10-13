using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var ledger = await RequestLedger.LoadAsync(brain, "voice");
await foreach (var request in brain.On(brain.Get<IApp>(Ino.Key(brain)), "voice", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    await brain.Get<IApp>(Ino.AssistantKey(brain)).Request("voice", request.Input);
    await ledger.Record(request.RequestId);
}
