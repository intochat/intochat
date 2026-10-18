using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// A registry change means the set of views (or their definitions) changed: refresh them all.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await brain.React<TextChanged>(brain.Get<IText>(appKey + "/views"), _ => app.Request("refresh", ""), brain.Stopping);
