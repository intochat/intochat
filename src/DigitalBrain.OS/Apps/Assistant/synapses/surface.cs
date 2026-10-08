using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Workspace;

// The assistant's window. The chat transcript is projected independently from IAgent.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var invoked in app.Invocations(brain, brain.Stopping))
{ if (invoked.Operation == "open") { await app.Respond(await OpenAsync(invoked.InvocationId, invoked.Input == "docked")); } }
return;

async Task<AppResponse> OpenAsync(Guid id, bool docked)
{
    await ComposeAsync();
    var surface = new UiChildRef(UIVocabulary.SurfaceType, appKey + "/surface");
    if (!docked)
    {
        var brainScope = appKey[..appKey.LastIndexOf("/apps/", StringComparison.Ordinal)];
        await brain.Get<IWorkspace>(brainScope)
            .EnsureOpenAsync("assistant", "Assistant", WindowReference.For(surface), CancellationToken.None);
    }
    return new AppResponse(id, JsonSerializer.Serialize(new
    {
        id = docked ? "assistant-docked" : "assistant", title = "Assistant", surface = surface.Name, docked,
    }), null);
}

async Task ComposeAsync()
{
    string Name(string part) => appKey + "/" + part;
    UiChildRef Ref(string kind, string part) => new(kind, Name(part));

    var selected = await brain.Get<IText>(Name("conversation")).Read();
    var context = Name("conversations/" + (selected.Markdown.Length == 0 ? "main" : selected.Markdown));
    var composer = brain.Get<IComposer>(Name("composer"));
    await composer.Configure(context, selected.Version);
    await brain.Get<IButton>(Name("new")).Set("New conversation", "new");
    var header = brain.Get<ILayout>(Name("header"));
    await header.Set(new LayoutDefinition("row",
        [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.ButtonType, "stop"), Ref(UIVocabulary.ButtonType, "new")], Extents: [0, 72, 160]), (await header.Read()).Revision);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column",
        [Ref(UIVocabulary.LayoutType, "header"), Ref(UIVocabulary.ChatType, "transcript"), Ref(UIVocabulary.ComposerType, "composer")],
        Extents: [40, 0, 152]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Assistant", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
}
