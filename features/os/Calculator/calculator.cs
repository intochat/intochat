using DigitalBrain.Flutter.Views;

// The calculator keypad: each button click grows or evaluates the expression. State is the keypad
// neuron (Keypad.Press), so a restarted behavior resumes from it and a resent click is pressed once.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
// Clicks are serialized by the loop itself: one reaction at a time, so no gate.
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(appKey + "/view"), brain.Stopping))
{
    if (!Keypad.Keys.Contains(clicked.Action)) { continue; }
    await Keypad.Press(brain, clicked.Action, clicked.RequestId);
}
