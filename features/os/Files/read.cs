using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;

// "read" shows a file's text in the content area; a .cs file is shown as C# source.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var request in brain.On(app, "read", brain.Stopping))
{
    try
    {
        await Store.Preview(brain, request.Input);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/content").Set("This file could not be read."); }
}
