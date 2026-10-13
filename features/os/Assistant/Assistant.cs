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
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
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

    // What the model may act on: the contracts this app composes, checked by the compiler. An app's
    // answer is neuron state (a text, a table, a chart, an image), so those parts are readable too.
    public static readonly string[] Contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!,
        typeof(IWorkspace).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!,
        typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!,
        typeof(IMcpServer).FullName!, typeof(IPageTools).FullName!, typeof(IText).FullName!,
        typeof(DigitalBrain.Flutter.Table.ITable).FullName!, typeof(DigitalBrain.Flutter.Chart.IChart).FullName!,
        typeof(DigitalBrain.Flutter.Image.IImage).FullName!];

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static double RouteGate(AppSnapshot snapshot)
        => double.TryParse(snapshot.Settings.GetValueOrDefault("RouteGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var gate)
            && gate is >= 0 and <= 1 ? gate : DefaultRouteGate;

    public static string Clip(string text) => text.Length <= 200 ? text : text[..200];

    // Starts the model's turn for a sent message, under the request id so a redelivered message replays
    // instead of running twice. A turn that cannot start (a response is already running, the Model
    // setting names an unavailable model) says so on the status line and never stops the synapse.
    public static async Task Answer(DigitalBrainConnection brain, AppSnapshot snapshot, string input, string hint, Guid requestId)
    {
        try
        {
            var runId = requestId.ToString("N");
            var source = Key(brain) + "/conversations/" + await ConversationId(brain);
            var conversation = brain.Get<IAgent>(source);
            if ((await conversation.Read()).ActiveRun is { } running)
            {
                // A redelivered message whose turn is still running is already being answered.
                if (running.RunId == runId) { return; }
                // The message is kept in an empty draft, so sending it later is one click; typing is never replaced.
                var composer = brain.Get<IComposer>(Key(brain) + "/composer");
                var draft = await composer.Read();
                var kept = draft.Draft.Length == 0 && draft.Context == source;
                if (kept) { await composer.Edit(source, input); }
                await Status(brain, kept
                    ? "A response is already running; your message is in the draft, send it when the response finishes."
                    : "A response is already running; send your message again when it finishes.");
                await brain.Get<IApp>(Key(brain)).Progress("attention", "A response is already running");
                return;
            }
            var choice = snapshot.Settings.GetValueOrDefault("Model", "");
            var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
            await conversation.Start(AgentTurn.Text(input, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", "") + hint, contracts: Contracts)
                with { Model = model, RunId = runId });
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await Status(brain, "Could not start the answer: " + Clip(error.Message));
            await brain.Get<IApp>(Key(brain)).Progress("attention", "Could not start the answer");
        }
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

    // Composing is idempotent: the parts are read together and only a part that differs is written, so a
    // repeated open is a handful of reads and announces no change to the shell.
    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        string Name(string part) => appKey + "/" + part;
        UiChildRef Ref(string kind, string part) => new(kind, Name(part));
        var composer = brain.Get<IComposer>(Name("composer"));
        var button = brain.Get<IButton>(Name("new"));
        var header = brain.Get<ILayout>(Name("header"));
        var main = brain.Get<ILayout>(Name("main"));
        var surface = brain.Get<ISurface>(Name("surface"));
        var selectedRead = brain.Get<IText>(Name("conversation")).Read();
        var composerRead = composer.Read();
        var buttonRead = button.Read();
        var headerRead = header.Read();
        var mainRead = main.Read();
        var surfaceRead = surface.Read();
        await Task.WhenAll(selectedRead, composerRead, buttonRead, headerRead, mainRead, surfaceRead);
        var selected = await selectedRead;
        var context = Name("conversations/" + (selected.Markdown.Length == 0 ? "main" : selected.Markdown));
        var headerDefinition = new LayoutDefinition("row", [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.ButtonType, "new")], Extents: [0, 160]);
        var mainDefinition = new LayoutDefinition("column",
            [Ref(UIVocabulary.LayoutType, "header"), Ref(UIVocabulary.ChatType, "transcript"), Ref(UIVocabulary.ComposerType, "composer")],
            Extents: [40, 0, 152]);
        var surfaceDefinition = new SurfaceDefinition("Assistant", [Ref(UIVocabulary.LayoutType, "main")]);
        var writes = new List<Task>();
        var composerState = await composerRead;
        if (composerState.Context != context || composerState.Generation != selected.Version) { writes.Add(composer.Configure(context, selected.Version)); }
        var buttonState = await buttonRead;
        if (buttonState.Label != "New conversation" || buttonState.Action != "new" || !buttonState.Enabled) { writes.Add(button.Set("New conversation", "new")); }
        var headerState = await headerRead;
        if (!Same(headerState.Definition, headerDefinition)) { writes.Add(header.Set(headerDefinition, headerState.Revision)); }
        var mainState = await mainRead;
        if (!Same(mainState.Definition, mainDefinition)) { writes.Add(main.Set(mainDefinition, mainState.Revision)); }
        var surfaceState = await surfaceRead;
        if (surfaceState.Definition.Title != surfaceDefinition.Title || !surfaceState.Definition.Children.SequenceEqual(surfaceDefinition.Children))
        { writes.Add(surface.Set(surfaceDefinition, surfaceState.Revision)); }
        await Task.WhenAll(writes);
        await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("surface", appKey + "/surface")));
    }

    private static bool Same(LayoutDefinition left, LayoutDefinition right)
        => left.Mode == right.Mode && left.Gap == right.Gap && left.FollowEnd == right.FollowEnd
            && left.Children.SequenceEqual(right.Children) && (left.Extents ?? []).SequenceEqual(right.Extents ?? []);

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

}
