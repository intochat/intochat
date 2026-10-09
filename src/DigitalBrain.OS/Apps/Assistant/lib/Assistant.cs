using System.Globalization;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;

// Shared by every synapse of the assistant: the app's key, its brain scope, the selected
// conversation and the composed surface. A synapse is one reaction; this is the vocabulary
// they all speak.
public static class Assistant
{
    public const double DefaultRouteGate = 0.8;

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static double RouteGate(AppSnapshot snapshot)
        => double.TryParse(snapshot.Settings.GetValueOrDefault("RouteGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var gate)
            && gate is >= 0 and <= 1 ? gate : DefaultRouteGate;

    // What Jev may carry out without the language model: an app operation, or a read that changes nothing.
    public static bool IsDirect(RouteResult route)
        => (route.Contract == typeof(IApp).FullName && route.Method == nameof(IApp.Request))
            || route.Method is "Read" or "List";

    public static Task Status(DigitalBrainConnection brain, string text)
        => brain.Get<IText>(Key(brain) + "/status").Set(text);

    public static async Task<string> ConversationId(DigitalBrainConnection brain)
    {
        var pointer = brain.Get<IText>(Key(brain) + "/conversation");
        var current = (await pointer.Read()).Markdown;
        if (current.Length > 0) { return current; }
        await pointer.Set("main");
        return "main";
    }

    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        string Name(string part) => appKey + "/" + part;
        UiChildRef Ref(string kind, string part) => new(kind, Name(part));
        var selected = await brain.Get<IText>(Name("conversation")).Read();
        var context = Name("conversations/" + (selected.Markdown.Length == 0 ? "main" : selected.Markdown));
        await brain.Get<IComposer>(Name("composer")).Configure(context, selected.Version);
        await brain.Get<IButton>(Name("new")).Set("New conversation", "new");
        var header = brain.Get<ILayout>(Name("header"));
        await header.Set(new LayoutDefinition("row", [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.ButtonType, "new")], Extents: [0, 160]), (await header.Read()).Revision);
        var main = brain.Get<ILayout>(Name("main"));
        await main.Set(new LayoutDefinition("column",
            [Ref(UIVocabulary.LayoutType, "header"), Ref(UIVocabulary.ChatType, "transcript"), Ref(UIVocabulary.ComposerType, "composer")],
            Extents: [40, 0, 152]), (await main.Read()).Revision);
        var surface = brain.Get<ISurface>(Name("surface"));
        await surface.Set(new SurfaceDefinition("Assistant", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
    }

    public static Task OpenWindowAsync(DigitalBrainConnection brain, CancellationToken ct)
        => brain.Get<IWorkspace>(Scope(brain)).EnsureOpenAsync(Key(brain) + "/window", "Assistant",
            WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Key(brain) + "/surface")), ct);
}
