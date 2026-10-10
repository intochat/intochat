using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;

// "open" composes the surface and shows the window; the roots are listed by browse.cs on its own request.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    var main = brain.Get<ILayout>(appKey + "/main");
    await main.Set(new("column", [new(UIVocabulary.TextType, appKey + "/location"),
        new(UIVocabulary.CollectionType, appKey + "/listing"), new(UIVocabulary.TextType, appKey + "/content")],
        Extents: [32, 0, 0]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(appKey + "/surface");
    await surface.Set(new("Files", [new(UIVocabulary.LayoutType, appKey + "/main")]), (await surface.Read()).Revision);
    await app.Request("browse", "");
    if (request.Input != "docked")
    {
        await brain.Get<IWorkspace>(brain.Setting("Brain")!).EnsureOpenAsync(appKey + "/window", "Files",
            WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, appKey + "/surface")), brain.Stopping);
    }
}
