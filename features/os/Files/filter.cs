using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.TextField.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
await foreach (var change in brain.On<TextFieldChanged>(brain.Get<ITextField>(appKey + "/filter"), brain.Stopping))
{
    var location = (await brain.Get<IText>(appKey + "/navigation").Read()).Markdown;
    await brain.Get<IApp>(appKey).Request(await Store.IsFolder(brain, location) ? "browse" : "read", location);
}
