using DigitalBrain.Apps;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// The New conversation button: the click becomes a request, and new.cs does the work. A redelivered
// click is skipped, so one click starts one conversation.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
var ledger = await RequestLedger.LoadAsync(brain, "new-button");
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Assistant.Key(brain) + "/new"), brain.Stopping))
{
    if (clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)) { continue; }
    await app.Request("new", "");
    await ledger.Record(clicked.RequestId);
}
