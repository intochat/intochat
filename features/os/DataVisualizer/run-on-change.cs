using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views.Signals;

// A registry change means the set of views (or their definitions) changed: refresh them all.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await brain.React<ViewChanged>(Views.RegistryView(brain, appKey), _ => app.Request("refresh", ""), brain.Stopping);
