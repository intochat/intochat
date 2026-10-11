using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// The calculator keypad: each button click grows or evaluates the expression. State is the keypad
// neuron (Keypad.Press), so a restarted behavior resumes from it and a resent click is pressed once.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var buttons = Keypad.Keys.Select((_, index) => (INeuron)brain.Get<IButton>(appKey + "/key-" + index)).ToArray();

// Clicks are serialized by the loop itself: one reaction at a time, so no gate.
await foreach (var clicked in brain.On<ButtonClicked>(Sources.Of(buttons), brain.Stopping)) { await Keypad.Press(brain, clicked.Action, clicked.RequestId); }
