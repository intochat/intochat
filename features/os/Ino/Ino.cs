using DigitalBrain;
using System.Globalization;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Facts;
using IntoChat.OS.Modules.AI.Agents.Intents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Integrations;
using IntoChat.OS.Modules.Microsoft.Windows;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Microsoft.Playwright;

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
        typeof(IntoChat.OS.Modules.Flutter.Table.ITable).FullName!, typeof(IntoChat.OS.Modules.Flutter.Chart.IChart).FullName!,
        typeof(IntoChat.OS.Modules.Flutter.Image.IImage).FullName!, typeof(IIntents).FullName!, typeof(IFacts).FullName!];

    public static double RouteGate(AppSnapshot snapshot)
        => double.TryParse(snapshot.Settings.GetValueOrDefault("RouteGate", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var gate)
            && gate is >= 0 and <= 1 ? gate : DefaultRouteGate;


    // Starts the model's turn for a sent message, under the request id so a redelivered message replays
    // instead of running twice. A turn that cannot start (a response is already running, the chat's
    // model or the Model setting is unavailable) says so on the status line and never stops the synapse.
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
                var kept = await KeepInDraft(brain, source, input);
                await Status(brain, kept ? InoCopy.AnswerRunning : InoCopy.AnswerRunningResend);
                await Advance(brain, "attention", "A response is already running");
                return AnswerOutcome.KeptInDraft;
            }
            // Only a chat's own unavailable choice is refused (kept, never silently replaced); an inherited
            // setting falls back to Auto with a note. NoModel (nothing can answer) shows the first-run card
            // instead: no turn, no failed status, the message kept in the draft.
            var resolved = await TurnModel(brain, snapshot, source);
            if (resolved.Outcome == InoModelOutcome.NoModel)
            {
                await KeepInDraft(brain, source, input);
                await Dirty(brain);
            }
            else if (resolved.Outcome != InoModelOutcome.Ready)
            {
                await KeepInDraft(brain, source, input);
                await Status(brain, resolved.Note ?? InoModels.UnavailableCopy);
                await Advance(brain, "attention", "The chosen model isn't available");
            }
            return await InoModels.Begin(resolved, async model =>
            {
                if (resolved.Note is { } note) { await Status(brain, note); }
                if (confirmed && intentId is { } dispatched) { await Dispatch(brain, dispatched, runId); }
                var instructions = snapshot.Files.GetValueOrDefault("prompts/system.md", "") + hint + (brief is null ? "" : BriefInstructions(brief, confirmed));
                var contracts = InoTools.Select(Contracts, await brain.Kernel.Types(brain.Stopping));
                await conversation.Start(AgentTurn.Text(input, instructions: instructions, contracts: contracts,
                        contexts: memoryKey is null ? null : [MemoryContextKey(memoryKey)])
                    with { Model = model, RunId = runId, Intent = intentId?.ToString() });
            });
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await Status(brain, Failure(InoCopy.StartFailed, error));
            await Advance(brain, "attention", "Could not start the answer");
            return AnswerOutcome.Failed;
        }
    }

    // The message is kept in an empty draft, so sending it later is one click; typing is never replaced.
    private static async Task<bool> KeepInDraft(IntoChatOSConnection brain, string source, string input)
    {
        var composer = brain.Get<IComposer>(DataKey(brain) + "/composer");
        var draft = await composer.Read();
        var kept = draft.Draft.Length == 0 && draft.Context == source;
        if (kept) { await composer.Edit(source, draft.Generation, input); }
        return kept;
    }

    // The chat's choice, else the app setting Model, else Automatic; see InoModels.Resolve. An unreadable index
    // falls back to the app setting.
    public static async Task<InoModelResolution> TurnModel(IntoChatOSConnection brain, AppSnapshot snapshot, string agentKey)
        => await InoModels.Resolve(Models(brain), await ChatModelChoice(brain, agentKey), snapshot.Settings.GetValueOrDefault("Model", ""));

    private static async Task<string?> ChatModelChoice(IntoChatOSConnection brain, string agentKey)
    {
        if (!InoChats.TryIdOf(agentKey, out var id)) { return null; }
        try { return (await InoChats.Index(brain).Get(id))?.ModelChoice; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return null; }
    }

    // The header's model menu and the first-run card for the selected chat, from one catalog read (each
    // publish re-reads it, so Retry is a republish); an unreadable catalog shows neither.
    private static async Task<(UiNode? Menu, UiNode? FirstRun)> ModelSurface(IntoChatOSConnection brain, string agentKey)
    {
        try
        {
            var setting = (await brain.Get<IApp>(Key(brain)).Read()).Settings.GetValueOrDefault("Model", "");
            var choices = await Models(brain).List();
            var chatChoice = await ChatModelChoice(brain, agentKey);
            return (InoModels.Menu(choices, InoModels.Shown(choices, chatChoice, setting)),
                InoModels.FirstRun(choices, chatChoice, setting, DataKey(brain)));
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return (null, null); }
    }

    // Arms the card's two shell handoffs: Settings on the API keys tab, and the Ollama download page.
    private static async Task SetupButtons(IntoChatOSConnection brain)
    {
        await Arm(brain.Get<IButton>(DataKey(brain) + InoModels.KeysButton), InoModels.KeysLabel, InoModels.KeysActivation(Scope(brain)));
        await Arm(brain.Get<IButton>(DataKey(brain) + InoModels.InstallOllamaButton), InoModels.InstallOllamaLabel, InoModels.InstallOllamaActivation);
        static async Task Arm(IButton button, string label, string activation)
        {
            var current = await button.Read();
            if (current.Activation != activation || current.Label != label) { await button.SetActivation(label, activation); }
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
            if (Interlocked.Exchange(ref _toldMemoryMissing, 1) == 0) { await Status(brain, InoCopy.MemoryMissing); }
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
        // The first message would create the entry too, so a failed write never fails the new chat.
        try { await InoChats.Index(brain).Create(InoChats.IdOf(source), ChatIndex.NewChat); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        await Dirty(brain);
    }

    // Tells view.cs, the single view writer, to republish. Best effort: the next change republishes anyway.
    public static async Task Dirty(IntoChatOSConnection brain)
    {
        try { await InoViewState.MarkDirty(InoViewState.Text(brain), DateTimeOffset.UtcNow); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }

    // A user message marks its chat active (and titles a new one); index.cs marks the view dirty on the change.
    public static async Task Touched(IntoChatOSConnection brain, string agentKey, string message)
    {
        if (!InoChats.TryIdOf(agentKey, out var id)) { return; }
        try { await InoChats.Index(brain).Touch(id, message); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }

    // Called only by view.cs, the single view writer. State is read here, after the writer's gate, so the tree
    // reflects every committed change; an unreadable index shows no chats. Returns when a shown toast expires.
    public static async Task<DateTimeOffset?> PublishView(IntoChatOSConnection brain)
    {
        var data = DataKey(brain);
        var selection = await EnsureSelection(brain);
        var state = await InoViewState.Read(InoViewState.Text(brain), DateTimeOffset.UtcNow);
        IReadOnlyList<ChatIndexEntry> chats;
        try { chats = (await InoChats.Index(brain).List(false, 0, ChatIndex.MaxPage)).Entries; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { chats = []; }
        InoChats.TryIdOf(selection.Agent, out var selected);
        var (menu, firstRun) = await ModelSurface(brain, selection.Agent);
        var model = new InoViewModel(chats, selected, state.Query, menu,
            RenamingId: state.RenamingId, Toast: state.Toast, UndoId: state.UndoId, FirstRun: firstRun);
        await new ViewPublisher(brain.Get<IView>(Key(brain) + "/view"), ProgramRevision(brain)).Publish(InoView.Render(data, model));
        return state.ToastUntil;
    }

    private static async Task Field(IntoChatOSConnection brain, string name, string label)
    {
        var field = brain.Get<ITextField>(DataKey(brain) + name);
        if ((await field.Read()).Label != label) { await field.Configure(label, "text"); }
    }

    // "open" with input "greet" (the desktop's first run) posts one greeting; the fixed id keeps it to one.
    public static async Task Greet(IntoChatOSConnection brain)
        => await brain.Get<IAgent>((await EnsureSelection(brain)).Agent).Post(InoCopy.Greeting, "greeting");

    public static async Task ComposeAsync(IntoChatOSConnection brain)
    {
        var key = Key(brain);
        await EnsureSelection(brain);
        await brain.Get<IComposer>(DataKey(brain) + "/composer").Follow(DataKey(brain) + "/transcript");
        await Field(brain, "/chat-search", "Search chats");
        await Field(brain, "/rename", "Chat title");
        await brain.Get<IApp>(key).Present(ProgramRevision(brain),
            new(new Sphere("icon.svg", ["mood"], Presence()),
                Main: new(UIVocabulary.ViewType, key + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, key + "/view"))]));
        // After Present and off the open path: the first-run card's handoffs and indexing old conversations run in
        // the background, so greet and compose never wait on them. A new entry raises ChatIndexChanged, so index.cs
        // marks the view dirty; the selected chat first, then every other (an upgraded 3.1.3 brain has none indexed).
        _ = InoChats.Background("setup buttons", _ => SetupButtons(brain), brain.Stopping);
        _ = InoChats.Background("back-fill", async token =>
        {
            await InoChats.Within(InoChats.BackfillTimeout, inner => InoChats.TryBackfill(brain, inner), token);
            await InoChats.Within(InoChats.BackfillAllTimeout, async inner => await InoChats.TryBackfillAll(brain, inner) >= 0, token);
            await Dirty(brain);
        }, brain.Stopping);
        await Dirty(brain);
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
