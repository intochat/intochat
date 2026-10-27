using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;

// The view publisher owns rendering. Opening only advertises its stable root.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await Present();
await foreach (var request in brain.On(app, "open", brain.Stopping)) { await Present(); }

Task Present() => app.Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
    new(new Sphere("icon.svg", []), Main: new(UIVocabulary.ViewType, appKey + "/view"),
        Views: [new("full", new(UIVocabulary.ViewType, appKey + "/view"))]));
