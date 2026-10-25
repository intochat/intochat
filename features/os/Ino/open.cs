using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await Ino.ComposeAsync(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await Ino.ComposeAsync(brain);
        if (request.Input == "greet") { await Ino.Greet(brain); }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        var copy = Ino.Failure(InoCopy.OpenFailed, error);
        await app.FailPresentation(Ino.ProgramRevision(brain), copy);
        await Ino.Status(brain, copy);
    }
}
