using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await Room.Compose(brain);
await Room.Project(brain);
await foreach (var request in brain.On(brain.Get<IApp>(Room.Key(brain)), "open", brain.Stopping))
{
    try
    {
        await Room.Compose(brain);
        await Room.Project(brain);
        var selected = (await brain.Get<IText>(Room.Key(brain) + "/navigation").Read()).Markdown;
        if (!string.IsNullOrEmpty(selected)) await Room.ShowPublication(brain, PublicationUi.Decode<AppPublicationRef>(selected));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Room.Status(brain, error.Message); }
}
