using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Microsoft.Playwright;

// Open: connect the app's MCP server (its tools join the brain's search), then publish the declarative
// view hosting the FilmCraft web build.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;

await FilmCraftView.Publish(brain, "FilmCraft is not connected. Request open to connect its tools.", "http://localhost:18083");

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        var session = await brain.Get<IArtCraftStudio>(brain.Setting("Brain")! + "/artcraft").Open("filmcraft", brain.Stopping);
        var status = session.WebUrl is null
            ? "FilmCraft tools connected; no page is served in this deployment."
            : "FilmCraft tools connected.";
        await brain.Get<IPageTools>(brain.Setting("Brain")! + "/pages/filmcraft")
            .Configure(new PageToolsSpec("FilmCraft page", Name("browser"), "filmcraft"), brain.Stopping);
        await FilmCraftView.Publish(brain, status, session.WebUrl ?? "http://localhost:18083");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await FilmCraftView.Publish(brain, error.Message, "http://localhost:18083");
    }
}
