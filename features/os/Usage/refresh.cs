using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Usage.Key(brain) + "/refresh"), brain.Stopping))
{
    await Usage.Compose(brain);
}
