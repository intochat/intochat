using DigitalBrain.Apps;
using DigitalBrain.ArtCraft;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Flutter.Workspace;

// Open: connect the app's MCP server (its tools join the brain's search), then show the app's own web
// build in the shell's browser. Plain grain calls, safe to re-run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
string Name(string part) => appKey + "/" + part;
UiChildRef Ref(string kind, string part) => new(kind, Name(part));

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    var status = brain.Get<IText>(Name("status"));
    try
    {
        var session = await brain.Get<IArtCraftStudio>(brain.Setting("Brain")! + "/artcraft").Open("photocraft", brain.Stopping);
        await status.Set(session.WebUrl is null ? "PhotoCraft tools connected; no page is served in this deployment." : "PhotoCraft tools connected.");
        var browser = brain.Get<IWebBrowser>(Name("browser"));
        await browser.Configure(Name("browser"));
        if (session.WebUrl is { } url) { await browser.Navigate(url, "PhotoCraft"); }
        var main = brain.Get<ILayout>(Name("main"));
        await main.Set(new LayoutDefinition("column", [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.WebBrowserType, "browser")],
            Extents: [40, 0]), (await main.Read()).Revision);
        var surface = brain.Get<ISurface>(Name("surface"));
        await surface.Set(new SurfaceDefinition("PhotoCraft", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
        await brain.Get<IWorkspace>(brain.Setting("Brain")!)
            .EnsureOpenAsync(Name("window"), "PhotoCraft", WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Name("surface"))), brain.Stopping);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set(error.Message); }
}
