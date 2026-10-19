using System.Globalization;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Facts;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;
using IntoChat.OS.Flame.Scene;
using IntoChat.OS;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Form;
using IntoChat.OS.Integrations;
using IntoChat.OS.Microsoft.Windows;
using IntoChat.OS.Microsoft.CSharp.Files;
using IntoChat.OS.Postgres;
using IntoChat.OS.Mcp;
using IntoChat.OS.Microsoft.Playwright;

// Shared by every synapse of the assistant: the app's key, its brain scope, the selected
// conversation and the composed surface. A synapse is one reaction; this is the vocabulary
// they all speak.
public static partial class Ino
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
        typeof(IntoChat.OS.Flutter.Table.ITable).FullName!, typeof(IntoChat.OS.Flutter.Chart.IChart).FullName!,
        typeof(IntoChat.OS.Flutter.Image.IImage).FullName!, typeof(IIntents).FullName!, typeof(IFacts).FullName!];


    public static double RouteGate(AppSnapshot snapshot)
        => double.TryParse(snapshot.Settings.GetValueOrDefault("RouteGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var gate)
            && gate is >= 0 and <= 1 ? gate : DefaultRouteGate;

    public static string Clip(string text) => text.Length <= 200 ? text : text[..200];

    // Starts the model's turn for a sent message, under the request id so a redelivered message replays
    // instead of running twice. A turn that cannot start (a response is already running, the Model
    // setting names an unavailable model) says so on the status line and never stops the synapse.
    public static async Task<AnswerOutcome> Answer(IntoChatOSConnection brain, AppSnapshot snapshot, string input, string hint, Guid requestId,
        IntentBrief? brief = null, bool confirmed = false, string? memoryKey = null, Guid? intentId = null)
    {
        try
        {
            var runId = requestId.ToString("N");
            var source = (await EnsureSelection(brain)).Agent;
            var conversation = brain.Get<IAgent>(source);
            if ((await conversation.Read()).ActiveRun is { } running)
            {
                // A redelivered message whose turn is still running is already being answered.
                if (running.RunId == runId) { return AnswerOutcome.Started; }
                // The message is kept in an empty draft, so sending it later is one click; typing is never replaced.
                var composer = brain.Get<IComposer>(DataKey(brain) + "/composer");
                var draft = await composer.Read();
                var kept = draft.Draft.Length == 0 && draft.Context == source;
                if (kept) { await composer.Edit(source, draft.Generation, input); }
                await Status(brain, kept
                    ? "A response is already running; your message is in the draft, send it when the response finishes."
                    : "A response is already running; send your message again when it finishes.");
                await Advance(brain, "attention", "A response is already running");
                return AnswerOutcome.KeptInDraft;
            }
            var choice = snapshot.Settings.GetValueOrDefault("Model", "");
            var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
            if (confirmed && intentId is { } dispatched) { await Dispatch(brain, dispatched, runId); }
            var instructions = snapshot.Files.GetValueOrDefault("prompts/system.md", "") + hint + (brief is null ? "" : BriefInstructions(brief, confirmed));
            var contracts = InoTools.Select(Contracts, await brain.Kernel.Types(brain.Stopping));
            await conversation.Start(AgentTurn.Text(input, instructions: instructions, contracts: contracts,
                    contexts: memoryKey is null ? null : [MemoryContextKey(memoryKey)])
                with { Model = model, RunId = runId, Intent = intentId?.ToString() });
            return AnswerOutcome.Started;
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await Status(brain, "Could not start the answer: " + Clip(error.Message));
            await Advance(brain, "attention", "Could not start the answer");
            return AnswerOutcome.Failed;
        }
    }

    // Memory is an app like any other: present when its manifest has a title.
    public static async Task<string?> MemoryKey(IntoChatOSConnection brain)
    {
        var key = IApp.Key(Scope(brain), "memory");
        return (await brain.Get<IApp>(key).Read()).Manifest.Title.Length > 0 ? key : null;
    }

    public static string MemoryContextKey(string memoryKey) => memoryKey + "/context";

    public static TimeSpan IntendGate(AppSnapshot snapshot)
        => TimeSpan.FromSeconds(double.TryParse(snapshot.Settings.GetValueOrDefault("IntendGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 ? seconds : 8);

    private static int _toldMemoryMissing;

    // Hands the message to Memory to intend before the model answers: the pending record is what intended.cs
    // answers from, when Memory's Rephrased signal arrives or, failing that, after IntendGate. False when
    // Memory is not installed, so the caller answers directly. The request id is sent in the "N" form, the
    // same form intended.cs matches IntentRephrased.RequestId against.
    public static async Task<bool> Intend(IntoChatOSConnection brain, AppSnapshot snapshot, string input, string hint, Guid requestId,
        string conversation, string synapse = "routed")
    {
        if (await MemoryKey(brain) is not { } memoryKey)
        {
            if (Interlocked.Exchange(ref _toldMemoryMissing, 1) == 0) { await Status(brain, "Memory is not installed; answering directly"); }
            return false;
        }
        await PendingStore.Write(brain, synapse, requestId, new(input, hint, conversation, DateTimeOffset.UtcNow, false));
        await brain.Get<IApp>(memoryKey).Request("intend",
            JsonSerializer.Serialize(new { message = input, requestId = requestId.ToString("N"), conversation }));
        return true;
    }

    // A confirmed intent moves to Dispatched as its turn starts (one already Dispatched is done: success); a conflicting write is read again and retried, and
    // a third conflict fails the answer (Failed) so the request is released and tried again.
    private static async Task Dispatch(IntoChatOSConnection brain, Guid intentId, string runId)
    {
        var intents = brain.Get<IIntents>(IIntents.Key(Scope(brain)));
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await intents.Read();
            if (snapshot.Intents.FirstOrDefault(intent => intent.Id == intentId) is not { State: IntentState.Confirmed }) { return; }
            try { await intents.Transition(intentId, IntentState.Dispatched, "turn " + runId, snapshot.Revision); return; }
            catch (RevisionConflictException) when (attempt < 3) { }
        }
    }

    public static Task Status(IntoChatOSConnection brain, string text)
        => brain.Get<IText>(DataKey(brain) + "/status").Set(text);

    public static long ProgramRevision(IntoChatOSConnection brain)
        => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    public static async Task<ChatSelection> EnsureSelection(IntoChatOSConnection brain)
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

    public static async Task NewConversation(IntoChatOSConnection brain, Guid operationId)
    {
        var chat = brain.Get<IChat>(DataKey(brain) + "/transcript");
        var current = await EnsureSelection(brain);
        var source = DataKey(brain) + "/conversations/c-" + operationId.ToString("N");
        var selected = await chat.SelectAgent(source, current.Revision, operationId);
        if ((await chat.Read()).Selection == selected) { await Status(brain, ""); }
    }

    public const string Greeting = "Hi, I'm Ino. Find and open apps in the Apps gallery in the dock, or just tell me what you need.";

    // "open" with input "greet" (the desktop's first run) posts one greeting; the fixed id keeps it to one.
    public static async Task Greet(IntoChatOSConnection brain)
        => await brain.Get<IAgent>((await EnsureSelection(brain)).Agent).Post(Greeting, "greeting");

    public static async Task ComposeAsync(IntoChatOSConnection brain)
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

    // Mood is part of the activity write. A second script that watches the activity and writes the
    // blackboard again starts another behavior for every state the sphere already received.
    public static Task Advance(IntoChatOSConnection brain, string state, string detail)
        => brain.Get<IApp>(Key(brain)).Progress(state, detail, new() { ["mood"] = MoodOf(state) });
}
