using System.Globalization;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Collection;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.TextField;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

// Startup composes and publishes both slots independently of workspace navigation.
var main = brain.Get<ILayout>(appKey + "/main");
var filter = brain.Get<ITextField>(appKey + "/filter");
if ((await filter.Read()).Label != "Filter names") { await filter.Configure("Filter names", "text"); }
await main.Set(new("column", [new(UIVocabulary.TextFieldType, appKey + "/filter"),
    new(UIVocabulary.CollectionType, appKey + "/listing"), new(UIVocabulary.TextType, appKey + "/content")],
    Extents: [64, 0, 0]), (await main.Read()).Revision);
var full = brain.Get<ILayout>(appKey + "/full");
await full.Set(new("column", [new(UIVocabulary.CollectionType, appKey + "/storages"), new(UIVocabulary.LayoutType, appKey + "/main")], Extents: [120, 0]), (await full.Read()).Revision);
await app.Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture),
    new(new Sphere("icon.svg", []), Side: new(UIVocabulary.CollectionType, appKey + "/storages"),
        Main: new(UIVocabulary.LayoutType, appKey + "/main"), Views: [new("full", new(UIVocabulary.LayoutType, appKey + "/full"))]));
if ((await brain.Get<ICollectionView>(appKey + "/listing").Read()).Revision == 0) { await app.Request("browse", ""); }

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await app.Request("browse", "");
}
