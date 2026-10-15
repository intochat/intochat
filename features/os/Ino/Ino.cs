using System.Globalization;
using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Views;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flame.Scene;
using DigitalBrain;
using DigitalBrain.Flutter.Text;
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
public static class Ino
{
    public const double DefaultRouteGate = 0.8;

    // Jev routes short commands; a longer message goes straight to the model.
    public const int MaxRoutedChars = 2000;

    // What the model may act on: the contracts this app composes, checked by the compiler. An app's
    // answer is neuron state (a text, a table, a chart, an image), so those parts are readable too.
    public static readonly string[] Contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!,
        typeof(IWorkspace).FullName!, typeof(IAppStudio).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!,
        typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!,
        typeof(IMcpServer).FullName!, typeof(IPageTools).FullName!, typeof(IText).FullName!,
        typeof(DigitalBrain.Flutter.Table.ITable).FullName!, typeof(DigitalBrain.Flutter.Chart.IChart).FullName!,
        typeof(DigitalBrain.Flutter.Image.IImage).FullName!];

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string DataKey(DigitalBrainConnection brain) => brain.Setting("ChatDataApp") is { Length: > 0 } data ? data : Key(brain);
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
            var source = (await EnsureSelection(brain)).Agent;
            var conversation = brain.Get<IAgent>(source);
            if ((await conversation.Read()).ActiveRun is { } running)
            {
                // A redelivered message whose turn is still running is already being answered.
                if (running.RunId == runId) { return; }
                // The message is kept in an empty draft, so sending it later is one click; typing is never replaced.
                var composer = brain.Get<IComposer>(DataKey(brain) + "/composer");
                var draft = await composer.Read();
                var kept = draft.Draft.Length == 0 && draft.Context == source;
                if (kept) { await composer.Edit(source, draft.Generation, input); }
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
        => brain.Get<IText>(DataKey(brain) + "/status").Set(text);

    public static long ProgramRevision(DigitalBrainConnection brain)
        => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    public static async Task<ChatSelection> EnsureSelection(DigitalBrainConnection brain)
    {
        var chat = brain.Get<IChat>(DataKey(brain) + "/transcript");
        if ((await chat.Read()).Selection is { } current) { return current; }
        try
        {
            return await chat.SelectAgent(
                DataKey(brain) + "/conversations/main", 0, Guid.NewGuid());
        }
        catch (RevisionConflictException)
        {
            return (await chat.Read()).Selection
                ?? throw new InvalidOperationException("Chat selection was not initialized.");
        }
    }

    public static async Task NewConversation(DigitalBrainConnection brain, Guid operationId)
    {
        var chat = brain.Get<IChat>(DataKey(brain) + "/transcript");
        var current = await EnsureSelection(brain);
        var source = DataKey(brain) + "/conversations/c-" + operationId.ToString("N");
        var selected = await chat.SelectAgent(source, current.Revision, operationId);
        if ((await chat.Read()).Selection == selected) { await Status(brain, ""); }
    }

    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var key = Key(brain);
        await EnsureSelection(brain);
        await brain.Get<IComposer>(DataKey(brain) + "/composer").Follow(DataKey(brain) + "/transcript");
        await new ViewPublisher(brain.Get<IView>(key + "/view"),
            ProgramRevision(brain)).Publish(InoView.Render(DataKey(brain)));
        await brain.Get<IApp>(key).Present(ProgramRevision(brain),
            new(new Sphere("icon.svg", ["mood"], Presence()),
                Main: new(UIVocabulary.ViewType, key + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, key + "/view"))]));
    }

    public static BehaviorTree Presence() => new(TreeNode.Selector(
        TreeNode.Sequence(false, TreeNode.Condition("mood", "listening"), TreeNode.Scale(1.08, 0.4), TreeNode.Scale(1.0, 0.4)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "thinking"), TreeNode.Pulse(0.12, 1.0)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "acting"), TreeNode.Pulse(0.2, 0.5)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "speaking"), TreeNode.Fade(0.8, 0.3), TreeNode.Fade(1.0, 0.3)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "error"), TreeNode.Scale(0.9, 0.2), TreeNode.Wait(0.6), TreeNode.Scale(1.0, 0.2)),
        TreeNode.Repeat(TreeNode.Wait(1))));

    public static string MoodOf(string activityState) => activityState switch
    {
        "listening" or "accepted" => "listening", "working" or "waiting" => "thinking",
        "reading" or "searching" => "acting", "completed" => "speaking", "attention" => "error", _ => "idle",
    };
}
