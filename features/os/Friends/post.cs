using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Friends.Key(brain));
await foreach (var request in brain.On(app, "post", brain.Stopping))
{ await Friends.Post(brain, request.RequestId, request.Input); }
