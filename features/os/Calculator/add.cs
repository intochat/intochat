using IntoChat.OS.Modules.Apps;

// "add" sums "a+b" (more terms are summed too) onto the declarative view surface. Input that is not a sum answers
// "Error" instead of throwing, so one bad request never stops the synapse or blocks the next one.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var request in brain.On(app, "add", brain.Stopping))
{
    await Keypad.Publish(brain, Keypad.Sum(request.Input));
}
