using System.Globalization;
using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var workspace = brain.Get<IFilesWorkspace>(appKey + "/workspace");
await workspace.Open(brain.Stopping);
await app.Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture),
    FilesEntry.Presentation(appKey));

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (await FilesEntry.Forward(workspace, request, brain.Stopping) is { } failure)
    { await app.Progress("failed", failure); }
}
