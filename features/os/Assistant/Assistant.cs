using System.Globalization;
using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Integrations;
using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Postgres;
using DigitalBrain.Mcp;
using DigitalBrain.Microsoft.Playwright;

// Shared by every synapse of the assistant: the app's key, its brain scope, the selected
// conversation and the composed surface. A synapse is one reaction; this is the vocabulary
// they all speak.
public static class Assistant
{
    public const double DefaultRouteGate = 0.8;

    // Jev routes short commands; a longer message goes straight to the model.
    public const int MaxRoutedChars = 2000;

    // What the model may act on: the contracts this app composes, checked by the compiler.
    public static readonly string[] Contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!,
        typeof(IWorkspace).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!,
        typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!,
        typeof(IMcpServer).FullName!, typeof(IPageTools).FullName!];

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static double RouteGate(AppSnapshot snapshot)
        => double.TryParse(snapshot.Settings.GetValueOrDefault("RouteGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var gate)
            && gate is >= 0 and <= 1 ? gate : DefaultRouteGate;

    public static async Task AskModel(DigitalBrainConnection brain, AppSnapshot snapshot, string input, string hint, Guid requestId)
    {
        var choice = snapshot.Settings.GetValueOrDefault("Model", "");
        var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
        var conversation = brain.Get<IAgent>(Key(brain) + "/conversations/" + await ConversationId(brain));
        await conversation.Start(AgentTurn.Text(input, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", "") + hint, contracts: Contracts)
            with { Model = model, RunId = requestId.ToString("N") });
    }

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

    // The selected conversation's agent, following the pointer text.
    public static Sources Conversation(DigitalBrainConnection brain)
        => Sources.Named<IText, TextChanged>(brain.Get<IText>(Key(brain) + "/conversation"), async (pointer, _) =>
        {
            var selected = (await pointer.Read()).Markdown;
            return [brain.Get<IAgent>(Key(brain) + "/conversations/" + (selected.Length == 0 ? "main" : selected))];
        });

    // Projects the selected conversation into the composer and transcript and returns the agent state,
    // so a synapse can tell on a rebuild whether its signal's side effect still applies.
    // IChat.Project ignores an unchanged (source, generation, version), so repeating it writes nothing.
    public static async Task<AgentState> Project(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        var pointer = await brain.Get<IText>(appKey + "/conversation").Read();
        var source = appKey + "/conversations/" + (pointer.Markdown.Length == 0 ? "main" : pointer.Markdown);
        var state = await brain.Get<IAgent>(source).Read();
        await brain.Get<IComposer>(appKey + "/composer").Configure(source, pointer.Version);
        var runs = state.Runs.ToList();
        if (state.ActiveRun is { } active) { runs.RemoveAll(run => run.RunId == active.RunId); runs.Add(active); }
        await brain.Get<IChat>(appKey + "/transcript").Project(new(source, pointer.Version, state.Version, [.. runs.SelectMany(run => new[]
        {
            new ChatEntry(run.RunId + "/user", ChatRole.User, run.UserText),
            new ChatEntry(run.RunId + "/assistant", ChatRole.Assistant, run.Reply ?? "")
            {
                Status = run.Status.ToString().ToLowerInvariant(), Error = run.Error,
                Activities = [.. run.ToolCalls.Select(call => new ChatActivity(call.CallId, call.Name, call.Arguments, call.Result, call.Error))],
            },
        })])
        {
            Actions = state.ActiveRun is { } running ? [new("stop/" + running.RunId, "Stop", JsonSerializer.Serialize(new { runId = running.RunId }))] : [],
        });
        return state;
    }

    // The last run's status when no run is active.
    public static AgentRunStatus? Settled(AgentState state)
        => state.ActiveRun is null ? state.Runs.LastOrDefault()?.Status : null;

    public static Task OpenWindowAsync(DigitalBrainConnection brain, CancellationToken ct)
        => brain.Get<IWorkspace>(Scope(brain)).EnsureOpenAsync(Key(brain) + "/window", "Assistant",
            WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Key(brain) + "/surface")), ct);
}
