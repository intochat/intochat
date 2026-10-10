using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.TextField;

// The Send button posts the prompt as an "ask" request.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(appKey + "/send"), brain.Stopping))
{
    if (clicked.RequestId == Guid.Empty) { continue; }
    var text = (await brain.Get<ITextField>(appKey + "/prompt").Read()).Value.Trim();
    if (text.Length is >= 1 and <= 32000) { await app.Request("ask", text); }
}
