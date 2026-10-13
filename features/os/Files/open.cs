using System.Globalization;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.TextField;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

// Startup composes and publishes both slots independently of workspace navigation.
var main = brain.Get<ILayout>(appKey + "/main");
var filter = brain.Get<ITextField>(appKey + "/filter");
if ((await filter.Read()).Label != "Filter names") { await filter.Configure("Filter names", "text"); }
await main.Set(new("column", [new(UIVocabulary.TextFieldType, appKey + "/filter"),
    new(UIVocabulary.CollectionType, appKey + "/listing"), new(UIVocabulary.TextType, appKey + "/content")],
    Extents: [64, 0, 0]), (await main.Read()).Revision);
await app.Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture),
    new(new Sphere("icon.svg", []), Side: new(UIVocabulary.CollectionType, appKey + "/storages"),
        Main: new(UIVocabulary.LayoutType, appKey + "/main")));
if ((await brain.Get<ICollectionView>(appKey + "/listing").Read()).Revision == 0) { await app.Request("browse", ""); }

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await app.Request("browse", "");
}
