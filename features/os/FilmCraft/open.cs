using DigitalBrain.Apps;
using DigitalBrain.ArtCraft;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Microsoft.Playwright;

// Open: connect the app's MCP server (its tools join the brain's search), then show the app's own web
// build in the shell's browser. Plain grain calls, safe to re-run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;
UiChildRef Ref(string kind, string part) => new(kind, Name(part));

await Compose();
if ((await brain.Get<IText>(Name("status")).Read()).Version == 0)
{ await brain.Get<IText>(Name("status")).Set("FilmCraft is not connected. Request open to connect its tools."); }

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    var status = brain.Get<IText>(Name("status"));
    try
    {
        var session = await brain.Get<IArtCraftStudio>(brain.Setting("Brain")! + "/artcraft").Open("filmcraft", brain.Stopping);
        await status.Set(session.WebUrl is null ? "FilmCraft tools connected; no page is served in this deployment." : "FilmCraft tools connected.");
        var browser = brain.Get<IWebBrowser>(Name("browser"));
        await browser.Configure(Name("browser"));
        if (session.WebUrl is { } url) { await browser.Navigate(url, "FilmCraft"); }
        // The page is the engine: its window.filmcraft API becomes tools on the same timeline the person sees.
        await brain.Get<IPageTools>(brain.Setting("Brain")! + "/pages/filmcraft").Configure(new PageToolsSpec("FilmCraft page", Name("browser"), "filmcraft"), brain.Stopping);
        await Compose();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set(error.Message); }
}

async Task Compose()
{
        var main = brain.Get<ILayout>(Name("main"));
        await main.Set(new LayoutDefinition("column", [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.WebBrowserType, "browser")],
            Extents: [40, 0]), (await main.Read()).Revision);
        await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));

}
