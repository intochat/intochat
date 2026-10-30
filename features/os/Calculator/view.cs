using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(brain.Setting("App")!);
await brain.React<AppPresented>(app, async _ =>
    await Keypad.Publish(brain, ""), brain.Stopping);
