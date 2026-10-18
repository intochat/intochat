using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Views;

// The dock's Clear button presses the keypad's "C" key, so every change to the expression goes through
// the one keypad synapse in click order. Before the first view exists, clear the durable state directly.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var view = brain.Get<IView>(appKey + "/view");
await foreach (var request in brain.On(app, "clear", brain.Stopping))
{
    if (await view.Read() is { } current)
    { await view.Activate(current.Generation, "key-" + Array.IndexOf(Keypad.Keys, "C"), "C", request.RequestId); }
    else { await Keypad.Press(brain, "C", request.RequestId); }
}
