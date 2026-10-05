#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;

// The assistant's window. Composition is plain grain calls, safe to re-run; the transcript's
// children belong to chat.cs, so composing never touches them.
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

    await brain.Get<ITextField>(Name("draft")).Configure("Message", "multiline", Name("send"));
    await brain.Get<IButton>(Name("send")).Set("Send", "send");
    await brain.Get<IButton>(Name("new")).Set("New conversation", "new");
    var input = brain.Get<ILayout>(Name("input"));
    await input.Set(new LayoutDefinition("row",
        [Ref(UIVocabulary.TextFieldType, "draft"), Ref(UIVocabulary.ButtonType, "send")], Extents: [0, 90]), (await input.Read()).Revision);
    var header = brain.Get<ILayout>(Name("header"));
    await header.Set(new LayoutDefinition("row",
        [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.ButtonType, "new")], Extents: [0, 160]), (await header.Read()).Revision);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column",
        [Ref(UIVocabulary.LayoutType, "header"), Ref(UIVocabulary.LayoutType, "transcript"), Ref(UIVocabulary.LayoutType, "input")],
        Extents: [40, 0, 96]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Assistant", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
}
