using DigitalBrain.Client;
using DigitalBrain.Flutter.Views;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var view = brain.Get<IView>(Ino.Key(brain) + "/view");
var ledger = await RequestLedger.LoadAsync(brain, "new-button");
await foreach (var clicked in brain.On<ViewAction>(view, brain.Stopping))
{
    if (clicked.Key != "new" || clicked.Action != "new"
        || clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)) { continue; }
    try { await Ino.NewConversation(brain, clicked.RequestId); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, "Could not start a new conversation: " + Ino.Clip(error.Message)); }
    await ledger.Record(clicked.RequestId);
}
