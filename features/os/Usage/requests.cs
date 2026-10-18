using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await Usage.Compose(brain);
await foreach (var request in brain.On<AppRequested>(brain.Get<IApp>(Usage.Key(brain)), brain.Stopping))
{
    if (request.Operation is "open" or "refresh") { await Usage.Compose(brain); }
}
