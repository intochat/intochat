/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, choose-model, save-provider, connect-db, query-db, new, new-conversation, switch-conversation, chats, toggle-devui, make, link, open-drawer, close-drawer, drawer-tab, pin-artifact, approve-engineer-pr, clear-context, answer-task-ask, new-specialist, import-skill, open-specialist, specialist-panel, new-squad, open-squad-plan, squad-add-task, squad-move-task, squad-toggle-task, chat-about-choice, toggle-sidebar, toggle-new-menu, toggle-history, toggle-favourite, toggle-pin, set-folder, archive-thread, restore-thread, clear-today, create-room, open-pinned-apps
Note: approve-engineer-pr is a [PersonOnly] UI-only drawer command and is intentionally omitted from InoModelSetup.BuildBundle.
*/
using IntoChat.OS.Host.Apps;
using IntoChat.OS.Host.Surface;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

[Neuron("app.ino")]
[Screen("app.ino", Title = "Ino", Icon = Icons.Sparkles, Category = "Intelligence", Dock = 1)]
public sealed partial class InoApp : AggregateScreen<InoAppState>, INavigatedTo<InoNavArgs>, INavigatedFrom
{
    private const string ApprovalsKey = "approvals/index", DataKey = "ino/data", ModelsKey = "main";

    [Slot(Key = "settings")] private partial IUserSettings UserSettings { get; }
    [Slot(Key = IInoRecords.Key)] private partial IInoRecords Records { get; }
    [Slot(Key = "compute", Required = false)] private partial ICompute? Compute { get; }
    [Slot(Key = DataKey)] private partial IData Data { get; }
    // The brain's workspace files: a query-db over a workspace file reads it here.
    [Slot(Key = WorkspaceFilesDefaults.Key, Required = false)] private partial IWorkspaceFiles? WorkspaceFiles { get; }
    [Slot(Key = "memory", Required = false)] private partial IMemory? Memory { get; }
    [Slot(Key = "main", Required = false)] private partial IAppStudio? AppStudio { get; }
    [Slot(Key = ModelsKey)] private partial IModelCatalog ModelCatalog { get; }
    [Slot(Key = IThreadDirectory.DefaultKey)] private partial IThreadDirectory SessionList { get; }
    [Slot(Key = IGrantsNeuron.DefaultKey)] private partial IGrantsNeuron Grants { get; }
    [Slot(Key = ApprovalsKey)] private partial IApprovals Approvals { get; }
    [Slot(Key = "ino/profile-writer")] private partial IProfileContextWriter ProfileWriter { get; }
    [Slot] private partial SlotCollection<IConversation> Conversations { get; }
    [Slot(Required = false)] private partial SlotCollection<IApp> Apps { get; }
    [Slot(Required = false)] private partial SlotCollection<IDevice> Devices { get; }
    [Slot(Required = false)] private partial SlotCollection<ICodeOrchestration> CodeOrchestrations { get; }
    [Slot(Key = ITasks.DefaultKey, Required = false)] private partial ITasks? Tasks { get; }
    [Slot(Key = IInstalledApps.DefaultKey, Required = false)] private partial IInstalledApps? InstalledApps { get; }
    [Slot(Key = IEngineerRuns.DefaultKey, Required = false)] private partial IEngineerRuns? EngineerRuns { get; }
    [Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)] private partial IConfirmationRequests? ConfirmationRequests { get; }
    [Slot(Key = "main", Required = false)] private partial IConnectionCatalog? ConnectionCatalog { get; }
    [Slot(Required = false)] private partial SlotCollection<IRepository> Repositories { get; }

    private ILogger<InoApp>? Logger { get; }
    private TimeProvider? Clock { get; }
    private MachineId? ThisMachine { get; }
    private InoSetupHost? SetupHost { get; set; }
    private ConversationView? ConversationView { get; set; }
    private long SeenRevision { get; set; }
    private ThreadPage? SessionsPage { get; set; }
    private IReadOnlyDictionary<string, AppManifest?> AppManifests { get; set; } = new Dictionary<string, AppManifest?>(StringComparer.Ordinal);
    private string? LinksJson { get; set; }
    private IReadOnlyList<ThreadMessage>? SyncedThread { get; set; }
    private ChatThreadProps? SyncedProps { get; set; }
    private ModelCard[]? PickerCards { get; set; }
    private ModelAvailability[]? PickerAvailability { get; set; }
    private DeviceView? DeviceSnapshot { get; set; }
    private string? ThreadFailureText { get; set; }
    private Rejected? RecordedRejection { get; set; }
    private string? CompletedRunId { get; set; }

    /// <summary>What the person reads when Ino could not write its conversation thread.</summary>
    public const string ThreadFailure = "The conversation could not be updated. Ino will try again with the next change.";

    public InoApp() : this(null, null, null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public InoApp(ILogger<InoApp>? logger = null, TimeProvider? time = null, MachineId? thisMachine = null)
    {
        Logger = logger;
        Clock = time;
        ThisMachine = thisMachine;
    }

    public VoiceAbilities Voice { get; private set; } = VoiceAbilities.None;
    public const string AppName = "ino";
    public static AppBundle Bundle => InoModelSetup.BuildBundle(typeof(InoApp));

    public new InoAppState State => base.State;
    public new Rejected? LastRejection => RecordedRejection ?? base.LastRejection;
    public View Render() => PreviewView(State);

    internal ConversationView? CurrentView => ConversationView;
    internal ThreadPage? CurrentSessions => SessionsPage;
    internal IReadOnlyDictionary<string, AppManifest?> CurrentApps => AppManifests;
    internal string? CurrentLinks => LinksJson;

    protected override Node Render(InoAppState state)
        => InoView.ScreenNode(
            state,
            Voice,
            props => Child<IChatThreadControl, ChatThreadProps>(InoView.ConversationKey, props),
            ConversationView,
            SessionsPage,
            ThreadFailureText,
            base.LastRejection,
            PickerCards,
            PickerAvailability,
            DeviceSnapshot,
            Child<IKanbanControl, KanbanProps>,
            Child<ICheckBoxControl, CheckBoxProps>,
            ThreadShelfSnapshot,
            ArchivedSessionsPage,
            ActiveThreadHostBrainId(state));

    internal string? ActiveThreadHostBrainId(InoAppState state)
    {
        var activeId = InoModel.ConversationIdOf(state.ConversationId);
        var entry = ThreadShelfSnapshot?.Entries.FirstOrDefault(e =>
            string.Equals(e.Id, activeId, StringComparison.Ordinal)
            || (e.Thread is { } thread && string.Equals(thread.Key, $"conversations/{activeId}", StringComparison.Ordinal)));
        if (entry?.Thread is { } activeThread && !string.IsNullOrWhiteSpace(activeThread.Brain.Value))
        {
            return activeThread.Brain.Value;
        }

        return CurrentBrain.Value;
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.CanDepart(departure.Force);
    }

    public async Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        RecordedRejection = null;
        if (CurrentBrain.Value is { Length: > 0 } && State.HasUnsavedProviderDraft)
        {
            await Commit(State with { HasUnsavedProviderDraft = false });
        }
    }

    public async Task OnNavigatedTo(NavigationArrival<InoNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        ActiveTransition = ViewTransition.Normalize(arrival.Transition);
        RecordedRejection = null;

        var attached = CurrentBrain.Value is { Length: > 0 };
        var args = arrival.Args ?? new InoNavArgs();
        if (!string.IsNullOrWhiteSpace(args.ConversationId))
        {
            var targetConversationId = InoModel.ConversationIdOf(args.ConversationId);
            if (!string.Equals(targetConversationId, State.ConversationId, StringComparison.Ordinal))
            {
                if (attached)
                {
                    await SwitchConversationAsync(targetConversationId, allowSameThreadRefresh: true);
                    var withArgs = State.ApplyNavigation(args);
                    if (!Equals(withArgs, State))
                    {
                        await Commit(withArgs);
                    }
                    return;
                }

                ResetConversationCache(targetConversationId);
                return;
            }
        }

        var next = State.ApplyNavigation(args);
        if (attached)
        {
            await Commit(next);
        }
    }

    private Task RecordRejectionAsync(Rejected rejection)
    {
        RecordedRejection = rejection;
        return PublishEphemeral(rejection);
    }

    // Saves Ino's state without messages and writes the projected conversation messages to the chat thread slot.
    private async Task Commit(InoAppState next)
    {
        RecordedRejection = null;
        var clean = next with { Messages = null };
        await Show(clean);
        var thread = InoView.Thread(clean, ConversationView, AppManifests, LinksJson);
        var viewKnown = ConversationView is not null;
        if (!viewKnown && thread.Count == 0) { return; }
        // The thread draws its stop and the error's retry from the props routed with the import: a props change writes it again too.
        var props = InoView.ThreadProps(clean, Voice, clean.ConversationId, ThreadFailureText, ConversationView, PickerCards, PickerAvailability, DeviceSnapshot);
        if (SyncedThread is { } synced && synced.SequenceEqual(thread) && Equals(props, SyncedProps)) { return; }
        try
        {
            await SendToThread(new ImportMessages(thread, Replace: viewKnown));
            SyncedThread = thread;
            SyncedProps = props;
            if (ThreadFailureText is not null) { ThreadFailureText = null; await Show(clean); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SyncedThread = null;
            SyncedProps = null;
            LogOptional(error, "conversation-thread");
            if (ThreadFailureText is null) { ThreadFailureText = ThreadFailure; await Show(clean); }
        }
    }

    private Task SendToThread(ImportMessages command)
        => Send(new Command(InoView.ConversationKey, "control.importMessages",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(command, typeof(ImportMessages))));

    [On]
    public Task OnThreadRetry(RetryRequested? requested)
        => requested is not null && SignalChild == InoView.ConversationKey ? RetryTurnAsync(requested.Id) : Task.CompletedTask;

    [On]
    public Task OnThreadSubmitted(Submitted? submitted)
        => submitted is not null && SignalChild == InoView.ConversationKey && submitted.Values.TryGetValue("text", out var text)
            ? Send(new InoSend(Message: text)) : Task.CompletedTask;

    [On]
    public Task OnThreadStop(StopRequested? requested)
        => requested is not null && SignalChild == InoView.ConversationKey ? Stop(new InoStop()) : Task.CompletedTask;

    [On]
    public Task OnThreadModelSelected(ChatModelSelected? selected)
        => selected is not null && SignalChild == InoView.ConversationKey
            ? ChooseModel(new InoChooseModel(Model: selected.ModelId, MakeDefault: selected.MakeDefault))
            : Task.CompletedTask;

    [On]
    public Task OnThreadContextCleared(ContextCleared? cleared)
        => cleared is not null && SignalChild == InoView.ConversationKey
            ? ClearContext(new InoClearContext())
            : Task.CompletedTask;

    [On]
    public Task OnConversationChanged(ConversationChanged? changed)
        => IsCurrentConversation(changed) ? RefreshConversationAsync(full: ConversationView is null) : Task.CompletedTask;

    private async Task RetryTurnAsync(string? id)
    {
        if (ConversationView is null)
        {
            await RefreshConversationAsync(full: true);
        }

        var latestRun = ConversationView?.Runs.LastOrDefault(static r => !r.Posted);
        if (!string.IsNullOrEmpty(id))
        {
            var entryId = new EntryId(id);
            if (latestRun is not null && entryId == EntryIds.Reply(latestRun.RunId))
            {
                await RegenerateLatestAsync(entryId);
                return;
            }

            if (InoModel.PromptOf(ConversationView, id) is { } prompt)
            {
                await Send(new InoSend(Message: prompt));
                return;
            }

            if (EntryIds.RunOf(entryId) is { Length: > 0 } runId && entryId == EntryIds.Reply(runId))
            {
                await RegenerateLatestAsync(entryId);
            }
            return;
        }

        if (latestRun is not null)
        {
            await RegenerateLatestAsync(EntryIds.Reply(latestRun.RunId));
            return;
        }

        var retryDecision = InoModel.DecideRetryPrompt(State, ConversationView);
        if (retryDecision.IsRefused)
        {
            await RecordRejectionAsync(retryDecision.Rejection);
            return;
        }
        await Send(new InoSend(Message: retryDecision.Next));
    }

    private async Task RegenerateLatestAsync(EntryId reply)
    {
        var conversationKey = InoModel.ConversationKeyOf(State.ConversationId);
        try
        {
            await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
            await Conversations[conversationKey].Regenerate(reply);
            await RefreshConversationAsync(full: false);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            await Commit(InoModel.StartFailed(State, new InoError("paused", error.Message[InoModel.BlockedPrefix.Length..])));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (Logger is { } log) { InoLog.LogTurnStartFailed(log, error, conversationKey); }
            var code = AgentFailures.CodeOf(AgentFailures.Classify(error));
            if (code == "provider-unavailable" && await InoModelSetup.IsMissingLocalOllamaAsync(Setup, null)) { code = "no-provider"; }
            await Commit(InoModel.StartFailed(State, new InoError(code)));
        }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await TryOptionalAsync("conversation-thread", async () =>
        {
            await FollowChild<IChatThreadControl, RetryRequested>(InoView.ConversationKey);
            await FollowChild<IChatThreadControl, Submitted>(InoView.ConversationKey);
            await FollowChild<IChatThreadControl, StopRequested>(InoView.ConversationKey);
            await FollowChild<IChatThreadControl, ChatModelSelected>(InoView.ConversationKey);
            await FollowChild<IChatThreadControl, ContextCleared>(InoView.ConversationKey);
        });
        await ActivateSquadControlsAsync();
        await TryOptionalAsync("settings", () => Follow<IUserSettings, UserSettingsChanged>("settings"));
        if (ThisMachine is { Value.Length: > 0 } machine)
        {
            await TryOptionalAsync("device-installs", () => Follow<IDevice, InstallChanged>($"devices/{machine.Value}"));
        }
        await TryOptionalAsync("confirmations-synapses", () => Context.Wires.EnsureBrainWide([typeof(ConfirmationRequested), typeof(ConfirmationResolved), typeof(FlowsChanged), typeof(TasksChanged), typeof(TaskAsked)]));
        var snapshot = await RefreshPickerAsync();
        var settings = await ReadOptionalAsync<UserSettingsState?>("settings-read", null, async () => await UserSettings.Read());
        Voice = await InoModelSetup.InitializeViewAsync(Setup, snapshot, settings);
        await InoModelSetup.OpenSessionAsync(Setup, State.ConversationId);
        var conversationKey = InoModel.ConversationKeyOf(State.ConversationId);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        await TrySeatFromSettingsAsync(State.ConversationId, settings, snapshot);
        await RefreshTasksAsync(commit: false);
        await RefreshConversationAsync(full: true);
        await ActivateArcSidebarAsync();
    }

    [On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await UserSettings.Read();
        var snapshot = await RefreshPickerAsync();
        var (_, effectiveModel, displayModel) = await InoModelSetup.ResolveForSettingsAsync(Setup, settings, snapshot);
        Voice = await InoModelSetup.ReadVoiceAsync(Setup, snapshot.Catalog?.Availability);
        var (nextState, migrate) = InoModel.DecideOpen(
            State, request.Mode, settings.DisplayName, displayModel, InoModelSetup.IsSmart(settings),
            await InoModelSetup.ReadApprovalsAsync(Setup, State.Approvals), await InoModelSetup.ReadGrantSummariesAsync(Setup, State.GrantSummaries));

        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations);
        var devUi = string.Equals(request.Mode, "devui", StringComparison.OrdinalIgnoreCase) || State.DevUiMode;
        await Commit(nextState with { RecentConversations = recents, DevUiMode = devUi });
        await TryOptionalAsync("conversation", () => InoModelSetup.ConfigureOpenConversationAsync(Setup, State.ConversationId, settings, effectiveModel, migrate));
        await RefreshConversationAsync(full: ConversationView is null);
    }

    [On]
    public async Task ShowChats(InoShowChats request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opening = !string.Equals(State.ActiveSection, "chats", StringComparison.Ordinal);
        if (opening)
        {
            SessionsPage = await InoModelSetup.ReadSessionsAsync(Setup) ?? new ThreadPage([], 0);
        }
        await Commit(State with { ActiveSection = opening ? "chats" : "chat" });
    }

    [On] public Task ToggleDevUi(InoToggleDevUi request) { ArgumentNullException.ThrowIfNull(request); return Commit(State.ToggleDevUi()); }
    [On] public async Task OnGrantsChanged(GrantsChanged? changed) { if (changed is not null) { await Commit(State with { GrantSummaries = await InoModelSetup.ReadGrantSummariesAsync(Setup, State.GrantSummaries) }); } }
    [On] public Task DismissDraft(InoDismissDraft request) { ArgumentNullException.ThrowIfNull(request); return Commit(State.DismissNavigationSeed()); }
    [On] public Task New(InoNew request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task NewConversation(InoNewConversation request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task SwitchConversation(InoSwitchConversation request) { ArgumentNullException.ThrowIfNull(request); return SwitchConversationAsync(request.ConversationId); }
    [On] public Task OnConfirmationRequested(ConfirmationRequested? requested) => requested is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;

    [On]
    public async Task OnConfirmationResolved(ConfirmationResolved? resolved)
    {
        if (resolved is not { PlanId.Length: > 0 }) { return; }
        await RefreshApprovalsAsync();
        await RefreshActiveEngineerDrawerIfMatchingAsync(planId: resolved.PlanId, flowId: null);
    }

    [On]
    public async Task OnFlowsChanged(FlowsChanged? changed)
    {
        if (changed is not { FlowId.Length: > 0 }) { return; }
        await RefreshApprovalsAsync(skipWhenBothEmpty: true);
        await RefreshActiveEngineerDrawerIfMatchingAsync(planId: null, flowId: changed.FlowId);
    }
    [On] public Task OnTasksChanged(TasksChanged? changed) => changed is { TaskId.Length: > 0 } ? RefreshTasksAsync() : Task.CompletedTask;
    [On] public Task OnTaskAsked(TaskAsked? asked) => asked is { TaskId.Length: > 0 } ? RefreshTasksAsync() : Task.CompletedTask;

    [On]
    public Task ToggleSpecialistPanel(InoToggleSpecialistPanel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode.Trim().ToLowerInvariant();
        var nextMode = mode is "new" or "import"
            ? (string.Equals(State.SpecialistPanelMode, mode, StringComparison.Ordinal) ? null : mode)
            : null;
        return Commit(State with { SpecialistPanelMode = nextMode, SpecialistError = null });
    }

    [On]
    public async Task CreateSpecialist(InoCreateSpecialist request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await ReadExistingSpecialistsAsync();
        var draft = new SpecialistDraft(
            Title: request.Title,
            Handle: request.Handle,
            Instructions: request.Instructions,
            Description: request.Description,
            AllowedContracts: request.AllowedContracts,
            ToolSources: request.ToolSources,
            MemoryScope: request.MemoryScope,
            Starters: request.Starters,
            Settings: request.Settings,
            Connections: request.Connections);
        var prepared = SpecialistAuthoring.PrepareCreate(draft, existing, brainId: CurrentBrain.Value);
        await CompleteSpecialistAuthoringAsync(prepared, request.Settings, request.Connections, panelMode: "new");
    }

    [On]
    public async Task ImportSkill(InoImportSkill request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await ReadExistingSpecialistsAsync();
        var prepared = SpecialistAuthoring.PrepareSkillImport(
            fileName: request.FileName,
            markdown: request.Content,
            existingSpecialists: existing,
            handleOverride: request.Handle,
            allowedContractsOverride: request.AllowedContracts,
            toolSourcesOverride: request.ToolSources,
            memoryScopeOverride: request.MemoryScope,
            includeTregSlot: false,
            brainId: CurrentBrain.Value);
        await CompleteSpecialistAuthoringAsync(prepared, settings: null, connections: null, panelMode: "import");
    }

    [On]
    public Task OpenSpecialist(InoOpenSpecialist request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            return RecordRejectionAsync(new Rejected("open-specialist", ["invalid_specialist"], ["Specify a specialist app id to open."]));
        }

        string conversationId;
        try
        {
            conversationId = AgentAppPolicy.ConversationOf(request.AppId.Trim()).Value;
        }
        catch (ArgumentException error)
        {
            return RecordRejectionAsync(new Rejected("open-specialist", ["invalid_specialist"], [error.Message]));
        }

        return SwitchConversationAsync(conversationId, allowSameThreadRefresh: true);
    }

    private async Task CompleteSpecialistAuthoringAsync(
        SpecialistAuthoringResult prepared,
        Dictionary<string, string>? settings,
        Dictionary<string, string>? connections,
        string panelMode)
    {
        if (!prepared.Succeeded || string.IsNullOrWhiteSpace(prepared.AppId) || prepared.Manifest is null)
        {
            var message = prepared.Error ?? "Could not validate specialist configuration.";
            await Commit(State with
            {
                SpecialistPanelMode = panelMode,
                SpecialistError = message,
                Status = message,
            });
            await RecordRejectionAsync(new Rejected("specialist-authoring", ["invalid_specialist"], [message]));
            return;
        }

        try
        {
            await SpecialistAuthoring.CreateAndConfigureAsync(
                prepared.AppId,
                prepared.Manifest,
                appId => Apps[AppNeuronIdOf(appId)],
                settings,
                connections);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "specialist-create");
            await Commit(State with
            {
                SpecialistPanelMode = panelMode,
                SpecialistError = error.Message,
                Status = error.Message,
            });
            await RecordRejectionAsync(new Rejected("specialist-authoring", ["specialist_create_failed"], [error.Message]));
            return;
        }

        var conversationId = AgentAppPolicy.ConversationOf(prepared.AppId).Value;
        await InoModelSetup.OpenSessionAsync(Setup, conversationId, prepared.Title);
        await Commit(State with { SpecialistPanelMode = null, SpecialistError = null });
        await SwitchConversationAsync(conversationId, allowSameThreadRefresh: true);
    }

    private async Task<List<(string AppId, string? Handle)>> ReadExistingSpecialistsAsync()
    {
        var result = new List<(string AppId, string? Handle)>();
        if (InstalledApps is null || CurrentBrain.Value is not { Length: > 0 })
        {
            return result;
        }

        var tiles = await ReadOptionalAsync<AppTile[]>("installed-apps", [], InstalledApps.List);
        foreach (var tile in tiles)
        {
            if (!string.Equals(tile.Kind, "agent", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tile.Id))
            {
                continue;
            }

            AppSnapshot? snapshot = null;
            await TryOptionalAsync("specialist-read", async () => snapshot = await Apps[AppNeuronIdOf(tile.Id)].Read());
            if (snapshot?.Manifest?.Agent is { } agent)
            {
                result.Add((tile.Id, agent.Handle));
            }
        }

        return result;
    }

    private NeuronId AppNeuronIdOf(string appId)
    {
        var fullKey = IntoChat.OS.Host.Primitives.AppRef.Of(CurrentBrain, appId).Key;
        return new NeuronId(CurrentBrain, AppGrainType.Name, fullKey[(CurrentBrain.Value.Length + 1)..]);
    }

    private IApp? TryGetAppNeuron(string appId)
        => CurrentBrain.Value is { Length: > 0 } ? Apps[AppNeuronIdOf(appId)] : null;

    [On]
    public async Task AnswerTaskAsk(InoAnswerTaskAsk request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taskId = request.TaskId?.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var option = request.Option?.Trim();
        if (string.IsNullOrWhiteSpace(option)
            && request.OptionIndex is { } idx
            && State.ActiveTasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal)) is { AskOptions: var opts }
            && idx >= 0 && idx < opts.Length)
        {
            option = opts[idx];
        }

        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        if (Tasks is { } tasks)
        {
            if (Context.Caller is not null
                && !Answerer.Of(Context.Caller, CurrentBrain).IsPerson)
            {
                throw new UnauthorizedAccessException("Only a signed-in person may answer a task question.");
            }

            await tasks.AnswerAsk(taskId, option);
            await RefreshTasksAsync();
        }
    }

    private async Task RefreshTasksAsync(bool commit = true)
    {
        if (Tasks is not { } tasks)
        {
            return;
        }

        var records = await ReadOptionalAsync<TaskRecord[]>("tasks-list", [], () => tasks.List());
        var isSquad = ConversationView is { Kind: ThreadKind.Squad } || State.ConversationId.StartsWith("squad-", StringComparison.Ordinal);
        var filtered = isSquad
            ? records.Where(r => string.IsNullOrWhiteSpace(r.ConversationId)
                || string.Equals(r.ConversationId, State.ConversationId, StringComparison.Ordinal))
            : records;
        var projected = filtered
            .Where(static r => r.Status is TaskStatus.Queued or TaskStatus.InProgress or TaskStatus.NeedsYou or TaskStatus.Waiting or TaskStatus.Failed or TaskStatus.Completed)
            .OrderByDescending(static r => r.Status == TaskStatus.NeedsYou)
            .ThenByDescending(static r => r.UpdatedAt)
            .Take(16)
            .Select(static r =>
            {
                var artifact = r.Artifacts.LastOrDefault();
                var statusSlug = r.Status switch
                {
                    TaskStatus.NeedsYou => "needs-you",
                    TaskStatus.InProgress => "in-progress",
                    TaskStatus.Waiting => "waiting",
                    TaskStatus.Completed => "completed",
                    TaskStatus.Failed => "failed",
                    _ => "queued",
                };
                return new InoTaskSummary(
                    TaskId: r.TaskId,
                    Title: r.Title,
                    Status: statusSlug,
                    Summary: r.Summary,
                    FlowId: r.FlowId,
                    ArtifactUri: artifact?.Uri,
                    ArtifactLabel: artifact?.Label,
                    AskQuestion: r.PendingAsk?.Question,
                    AskOptions: r.PendingAsk?.Options ?? [],
                    AskRecommendedIndex: r.PendingAsk?.RecommendedIndex,
                    ConfirmationPlanId: r.ConfirmationPlanId);
            })
            .ToArray();

        var engineerRuns = await ReadSquadEngineerRunsAsync(projected);
        var clearChatAbout = !string.IsNullOrWhiteSpace(State.ChattingAboutChoiceTaskId)
            && !projected.Any(t => string.Equals(t.TaskId, State.ChattingAboutChoiceTaskId, StringComparison.Ordinal)
                && string.Equals(t.Status, "needs-you", StringComparison.OrdinalIgnoreCase));

        var updated = State with
        {
            ActiveTasks = projected,
            SquadEngineerRuns = engineerRuns,
            ChattingAboutChoiceTaskId = clearChatAbout ? null : State.ChattingAboutChoiceTaskId,
        };

        if (!commit)
        {
            if (projected.Length > 0 || engineerRuns.Length > 0)
            {
                await Show(updated);
            }
            if (updated.DrawerOpen && string.Equals(updated.DrawerTab, InoSquads.DrawerTabPlan, StringComparison.Ordinal))
            {
                await SyncSquadCheckBoxesAsync(projected);
            }
            return;
        }

        await Commit(updated);
        if (updated.DrawerOpen && string.Equals(updated.DrawerTab, InoSquads.DrawerTabPlan, StringComparison.Ordinal))
        {
            await SyncSquadCheckBoxesAsync(projected);
        }
    }

    [On]
    public async Task OpenDrawer(InoOpenDrawer request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var explicitEngineerPr = string.Equals(request.Kind, InoDrawerArtifact.EngineerPrKind, StringComparison.OrdinalIgnoreCase);
        var flowId = string.IsNullOrWhiteSpace(request.FlowId) ? null : request.FlowId.Trim();

        if (explicitEngineerPr || InoCards.IsEngineerFlowId(flowId))
        {
            if (string.IsNullOrWhiteSpace(flowId))
            {
                await RecordRejectionAsync(new Rejected(
                    "open-drawer",
                    ["invalid_engineer_flow"],
                    ["A valid engineer run flowId is required to open the Engineer PR drawer."]));
                return;
            }

            if (EngineerRuns is not { } runs)
            {
                await RecordRejectionAsync(new Rejected(
                    "open-drawer",
                    ["engineer_runs_unavailable"],
                    ["Engineer runs service is not available."]));
                return;
            }

            EngineerRunSummary? summary = null;
            try
            {
                summary = await runs.InspectEngineerRun(flowId);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogOptional(error, "engineer-run-inspect");
                await RecordRejectionAsync(new Rejected(
                    "open-drawer",
                    ["engineer_run_inspect_failed"],
                    [$"Could not inspect engineer run '{flowId}': {error.Message}"]));
                return;
            }

            if (summary is null || !summary.Ok || string.IsNullOrWhiteSpace(summary.FlowId))
            {
                var detail = !string.IsNullOrWhiteSpace(summary?.SummaryText)
                    ? summary.SummaryText
                    : $"Engineer run '{flowId}' was not found.";
                await RecordRejectionAsync(new Rejected("open-drawer", ["engineer_run_not_found"], [detail]));
                return;
            }

            var hydrated = InoCards.FromEngineerRunSummary(summary);
            await Commit(State.OpenArtifactDrawer(hydrated) with { DrawerTab = "preview" });
            return;
        }

        InoDrawerArtifact? artifact = null;
        if (!string.IsNullOrWhiteSpace(flowId) && CurrentBrain.Value is { Length: > 0 })
        {
            var live = await ReadOptionalAsync<CodeOrchestrationView?>(
                "code-orchestration-read",
                null,
                () => CodeOrchestrations[ResolveCodeOrchestrationId(CurrentBrain, flowId)].ReadAsync());
            if (live is not null)
            {
                artifact = InoCards.FromCodeOrchestrationView(live);
            }
        }

        artifact ??= State.ActiveArtifact is { } current
            && ((!string.IsNullOrWhiteSpace(request.ArtifactId) && string.Equals(current.Id, request.ArtifactId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(flowId) && string.Equals(current.FlowId, flowId, StringComparison.Ordinal)))
            ? current
            : new InoDrawerArtifact(
                Id: !string.IsNullOrWhiteSpace(request.ArtifactId) ? request.ArtifactId.Trim() : (flowId ?? "artifact"),
                Title: !string.IsNullOrWhiteSpace(request.Title) ? request.Title.Trim() : (flowId ?? "Live Artifact"),
                Kind: !string.IsNullOrWhiteSpace(request.Kind) ? request.Kind.Trim() : "artifact",
                Status: "done",
                FlowId: flowId,
                LiveViewPath: string.IsNullOrWhiteSpace(request.LiveViewPath) ? null : request.LiveViewPath.Trim());

        await Commit(State.OpenArtifactDrawer(artifact));
    }

    [On]
    [PersonOnly("Approving an Engineer Draft PR publishes a SHA-locked candidate pull request on behalf of the brain owner.")]
    public async Task ApproveEngineerPr(InoApproveEngineerPr request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Context.Caller is null
            || !Answerer.Of(Context.Caller, CurrentBrain).IsPerson)
        {
            throw new UnauthorizedAccessException("ApproveEngineerPr is [PersonOnly] and may only be invoked directly by the brain owner.");
        }

        var flowId = !string.IsNullOrWhiteSpace(request.FlowId)
            ? request.FlowId.Trim()
            : State.ActiveArtifact is { Kind: InoDrawerArtifact.EngineerPrKind } activeFlow
                ? activeFlow.FlowId?.Trim()
                : null;
        if (string.IsNullOrWhiteSpace(flowId))
        {
            await RejectEngineerApprovalAsync("invalid_engineer_flow", "A valid engineer run flowId is required to approve a Draft PR.");
            return;
        }

        // Fail closed: caller must explicitly echo the reviewed commit SHA; never fall back to cached drawer state.
        var expectedSha = request.ExpectedCommitSha?.Trim();
        if (string.IsNullOrWhiteSpace(expectedSha))
        {
            await RejectEngineerApprovalAsync("missing_commit_sha", "An explicit reviewed candidate commit SHA is required before approving a Draft PR.");
            return;
        }

        if (EngineerRuns is not { } runs)
        {
            await RejectEngineerApprovalAsync("engineer_runs_unavailable", "Engineer runs service is not available.");
            return;
        }

        EngineerRunSummary? liveSummary;
        try
        {
            liveSummary = await runs.InspectEngineerRun(flowId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "engineer-run-inspect-approve");
            await RejectEngineerApprovalAsync("engineer_run_inspect_failed", $"Could not inspect engineer run '{flowId}': {error.Message}");
            return;
        }

        if (liveSummary is null || !liveSummary.Ok || string.IsNullOrWhiteSpace(liveSummary.FlowId))
        {
            var detail = !string.IsNullOrWhiteSpace(liveSummary?.SummaryText)
                ? liveSummary.SummaryText
                : $"Engineer run '{flowId}' was not found.";
            await RejectEngineerApprovalAsync("engineer_run_not_found", detail);
            return;
        }

        var liveArtifact = InoCards.FromEngineerRunSummary(liveSummary);

        if (InoCards.TryNormalizeHttpsPullRequestUrl(liveArtifact.PullRequestUrl, out var existingPrUrl))
        {
            await Commit(State.OpenArtifactDrawer(liveArtifact with { PullRequestUrl = existingPrUrl, ApprovalError = null, Status = "done" }) with
            {
                Status = $"Draft PR ready: {existingPrUrl}",
            });
            return;
        }

        if (liveSummary.Status != DigitalBrain.Flows.FlowStatus.Waiting)
        {
            var message = $"Cannot approve Draft PR: engineer run is in status '{liveSummary.Status}' rather than awaiting approval.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["flow_not_waiting"], [message]));
            return;
        }

        if (liveSummary.Receipt is not { } receipt)
        {
            var message = "Cannot approve Draft PR: EngineerVerificationReceipt is missing.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["missing_receipt"], [message]));
            return;
        }

        if (!receipt.BuildVerified)
        {
            var message = "Cannot approve Draft PR: build verification gate did not pass.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["build_not_verified"], [message]));
            return;
        }

        if (!receipt.LiveVerified)
        {
            var message = "Cannot approve Draft PR: live verification gate did not pass.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["live_not_verified"], [message]));
            return;
        }

        if (!receipt.SelfReviewPassed || liveSummary.SelfReview is { HasBlocking: true })
        {
            var message = $"Cannot approve Draft PR: LocalSelfReview gate failed ({liveArtifact.SelfReviewSummary ?? "blocking findings present"}).";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["self_review_failed"], [message]));
            return;
        }

        var liveReceiptSha = receipt.CommitSha?.Trim();
        if (string.IsNullOrWhiteSpace(liveReceiptSha)
            || !string.Equals(liveReceiptSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Commit SHA mismatch: reviewed '{ShortSha(expectedSha)}' no longer matches live run '{ShortSha(liveReceiptSha)}'. Re-inspect the updated diff and re-confirm before approving.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["sha_mismatch"], [message]));
            return;
        }

        // Primary path: EngineerFlow owns the approval-gate -> push -> pr pipeline via its direct-person ConfirmationPlanId.
        // Apps must never relay ConfirmationInbox.Resolve (07 D-09 / D-19 would deny as SelfApproval); instead, verify
        // the authoritative confirmation outcome on IConfirmationRequests and refresh the drawer from EngineerFlow.
        if (!string.IsNullOrWhiteSpace(liveSummary.ConfirmationPlanId))
        {
            var planId = liveSummary.ConfirmationPlanId.Trim();
            if (ConfirmationRequests is not { } confirmationRequests)
            {
                var message = "Confirmation service is unavailable; cannot verify EngineerFlow approval gate.";
                await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
                await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["confirmations_unavailable"], [message]));
                return;
            }

            ConfirmationResult? outcome;
            try
            {
                outcome = await confirmationRequests.Outcome(planId);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogOptional(error, "engineer-pr-confirm-outcome");
                var message = $"Could not read Draft PR confirmation gate outcome: {error.Message}";
                await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
                await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["confirmation_failed"], [message]));
                return;
            }

            if (outcome is null)
            {
                var message = $"Approve the SHA-locked confirmation card (plan '{planId}') in the drawer so EngineerFlow can push the branch and open the Draft PR.";
                await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
                await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["confirmation_required"], [message]));
                return;
            }

            if (!outcome.Approved)
            {
                var message = $"Draft PR confirmation plan '{planId}' was denied ({outcome.Reason}).";
                await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
                await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["confirmation_denied"], [message]));
                return;
            }

            var refreshedSummary = await ReadOptionalAsync<EngineerRunSummary?>(
                "engineer-run-reinspect",
                null,
                async () => await runs.InspectEngineerRun(flowId));
            var refreshedArtifact = refreshedSummary is { Ok: true }
                ? InoCards.FromEngineerRunSummary(refreshedSummary)
                : liveArtifact;

            if (InoCards.TryNormalizeHttpsPullRequestUrl(refreshedArtifact.PullRequestUrl, out var publishedPrUrl))
            {
                await Commit(State.OpenArtifactDrawer(refreshedArtifact with
                {
                    PullRequestUrl = publishedPrUrl,
                    ApprovalError = null,
                    Status = "done",
                }) with
                {
                    Status = $"Opened Draft PR: {publishedPrUrl}",
                });
            }
            else
            {
                await Commit(State.OpenArtifactDrawer(refreshedArtifact with
                {
                    ApprovalError = null,
                    Status = string.Equals(refreshedArtifact.Status, "waiting", StringComparison.Ordinal) ? "running" : refreshedArtifact.Status,
                }) with
                {
                    Status = "Approved Draft PR · pushing branch and creating PR...",
                });
            }

            return;
        }

        // Fallback path ONLY when no ConfirmationPlanId exists AND the branch is already pushed.
        if (string.IsNullOrWhiteSpace(liveArtifact.PushedRef))
        {
            var message = "Cannot open Draft PR directly: no confirmation plan is active and the candidate branch has not been pushed yet.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["branch_not_pushed"], [message]));
            return;
        }

        var baseBranch = !string.IsNullOrWhiteSpace(request.BaseBranch)
            ? request.BaseBranch.Trim()
            : liveArtifact.BaseBranch?.Trim();
        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            var message = "Cannot open Draft PR directly without an explicit base branch when no confirmation plan is active.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["missing_base_branch"], [message]));
            return;
        }

        var repo = await TryResolveRepositoryAsync(liveArtifact.RepoSlug);
        if (repo is null)
        {
            var message = "No unambiguous connected native GitHub repository matched this run. Connect or disambiguate GitHub in Settings before opening a Draft PR.";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["repository_unavailable"], [message]));
            return;
        }

        var headBranch = !string.IsNullOrWhiteSpace(liveArtifact.HeadBranch)
            ? liveArtifact.HeadBranch
            : $"ino/engineer-{liveSummary.RunId}";
        var prTitle = !string.IsNullOrWhiteSpace(liveSummary.Goal)
            ? (liveSummary.Goal.Trim().Length <= 120 ? liveSummary.Goal.Trim() : liveSummary.Goal.Trim()[..119] + "…")
            : liveArtifact.Title;
        var prBody = BuildEngineerDraftPrBody(liveSummary, liveArtifact);

        try
        {
            var created = await repo.CreatePullRequest(new CreatePullRequest(
                Title: prTitle,
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Body: prBody,
                Draft: true));
            if (!InoCards.TryNormalizeHttpsPullRequestUrl(created.Url, out var validatedPrUrl))
            {
                var invalidUrlMessage = "GitHub repository returned an invalid or non-HTTPS pull request URL.";
                await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = invalidUrlMessage }));
                await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["invalid_pr_url"], [invalidUrlMessage]));
                return;
            }

            var openedArtifact = liveArtifact with
            {
                PullRequestUrl = validatedPrUrl,
                ApprovalError = null,
                Status = "done",
            };
            await Commit(State.OpenArtifactDrawer(openedArtifact) with
            {
                Status = $"Opened Draft PR: {validatedPrUrl}",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "engineer-pr-create");
            var message = $"Could not open Draft PR: {error.Message}";
            await Commit(State.OpenArtifactDrawer(liveArtifact with { ApprovalError = message }));
            await RecordRejectionAsync(new Rejected("approve-engineer-pr", ["pr_create_failed"], [message]));
        }
    }

    private async Task RefreshActiveEngineerDrawerIfMatchingAsync(string? planId, string? flowId)
    {
        if (!State.DrawerOpen
            || State.ActiveArtifact is not { Kind: InoDrawerArtifact.EngineerPrKind, FlowId.Length: > 0 } active
            || EngineerRuns is not { } runs)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(flowId)
            && !string.Equals(active.FlowId, flowId, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(planId)
            && !string.IsNullOrWhiteSpace(active.ConfirmationPlanId)
            && !string.Equals(active.ConfirmationPlanId, planId, StringComparison.Ordinal))
        {
            return;
        }

        var refreshedSummary = await ReadOptionalAsync<EngineerRunSummary?>(
            "engineer-run-refresh",
            null,
            async () => await runs.InspectEngineerRun(active.FlowId));
        if (refreshedSummary is not { Ok: true })
        {
            return;
        }

        var refreshedArtifact = InoCards.FromEngineerRunSummary(refreshedSummary);
        if (!active.Equals(refreshedArtifact))
        {
            await Commit(State.OpenArtifactDrawer(refreshedArtifact));
        }
    }

    private async Task RejectEngineerApprovalAsync(string code, string message)
    {
        if (State.ActiveArtifact is { Kind: InoDrawerArtifact.EngineerPrKind } active)
        {
            await Commit(State with { ActiveArtifact = active with { ApprovalError = message } });
        }

        await RecordRejectionAsync(new Rejected("approve-engineer-pr", [code], [message]));
    }

    private async Task<IRepository?> TryResolveRepositoryAsync(string? repoSlug)
    {
        if (CurrentBrain.Value is not { Length: > 0 } || string.IsNullOrWhiteSpace(repoSlug))
        {
            return null;
        }

        var normalizedSlug = repoSlug.Trim();
        var isOwnerRepoSlug = normalizedSlug.Contains('/', StringComparison.Ordinal);

        var candidateKeys = new List<string>(6)
        {
            $"connection/github/{normalizedSlug}",
        };
        if (isOwnerRepoSlug)
        {
            var slash = normalizedSlug.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash + 1 < normalizedSlug.Length)
            {
                candidateKeys.Add($"connection/github/{normalizedSlug[(slash + 1)..]}");
            }
        }

        if (ConnectionCatalog is { } catalog)
        {
            await TryOptionalAsync("connection-catalog", async () =>
            {
                var view = await catalog.Read();
                foreach (var row in view.Rows)
                {
                    if (string.Equals(row.ConnectorId, "github", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(row.ConnectionId))
                    {
                        candidateKeys.Add($"connection/github/{Uri.EscapeDataString(row.ConnectionId.Trim())}");
                    }
                }
            });
        }

        candidateKeys.Add("connection/github/main");

        var matched = new List<(IRepository Repo, string CanonicalRepo)>(2);
        foreach (var key in candidateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = Repositories[new NeuronId(CurrentBrain, "github", key)];
                var repoView = await candidate.Read();
                if (!repoView.Connected || string.IsNullOrWhiteSpace(repoView.Repository))
                {
                    continue;
                }

                var canonicalRepo = repoView.Repository.Trim();
                bool isMatch;
                if (isOwnerRepoSlug)
                {
                    isMatch = string.Equals(canonicalRepo, normalizedSlug, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    var repoSlash = canonicalRepo.LastIndexOf('/');
                    var repoNameSegment = repoSlash >= 0 && repoSlash + 1 < canonicalRepo.Length
                        ? canonicalRepo[(repoSlash + 1)..]
                        : canonicalRepo;
                    isMatch = string.Equals(repoView.ConnectionId?.Trim(), normalizedSlug, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(repoNameSegment, normalizedSlug, StringComparison.OrdinalIgnoreCase);
                }

                if (!isMatch)
                {
                    continue;
                }

                if (!matched.Any(m => string.Equals(m.CanonicalRepo, canonicalRepo, StringComparison.OrdinalIgnoreCase)))
                {
                    matched.Add((candidate, canonicalRepo));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogOptional(error, $"github-repository:{key}");
            }
        }

        // Fail closed when zero or multiple distinct connected repositories match (ambiguous short slug).
        return matched.Count == 1 ? matched[0].Repo : null;
    }

    private static string BuildEngineerDraftPrBody(EngineerRunSummary summary, InoDrawerArtifact artifact)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Goal\n").AppendLine(string.IsNullOrWhiteSpace(summary.Goal) ? artifact.Title : summary.Goal.Trim());
        sb.AppendLine();
        sb.Append("- **Flow ID**: `").Append(summary.FlowId).AppendLine("`");
        sb.Append("- **Reviewed Commit SHA**: `").Append(artifact.CandidateSha).AppendLine("`");
        sb.Append("- **Local Self-Review**: ").AppendLine(artifact.SelfReviewSummary ?? "passed");
        if (!string.IsNullOrWhiteSpace(artifact.VerificationSummary))
        {
            sb.Append("- **Verification**: ").AppendLine(artifact.VerificationSummary);
        }

        return sb.ToString().Trim();
    }

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return "none";
        }

        var trimmed = sha.Trim();
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    [On]
    public Task CloseDrawer(InoCloseDrawer request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Commit(State.CloseArtifactDrawer());
    }

    [On]
    public async Task SelectDrawerTab(InoSelectDrawerTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = State.SelectDrawerTab(request.Tab ?? string.Empty);
        if (decision.IsRefused)
        {
            await RecordRejectionAsync(decision.Rejection);
            return;
        }

        await Commit(decision.Next);
    }

    [On]
    public Task ClearContext(InoClearContext request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Commit(State.ClearActiveContext());
    }

    [On]
    public async Task PinArtifact(InoPinArtifact request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flowId = !string.IsNullOrWhiteSpace(request.FlowId)
            ? request.FlowId.Trim()
            : State.ActiveArtifact?.FlowId;
        if (string.IsNullOrWhiteSpace(flowId))
        {
            if (State.ActiveArtifact is { } missingFlow)
            {
                await Commit(State with { ActiveArtifact = missingFlow with { PinError = "No active code orchestration flow to pin." } });
            }
            return;
        }

        if (Context.Caller is not null
            && !Answerer.Of(Context.Caller, CurrentBrain).IsPerson)
        {
            throw new UnauthorizedAccessException("PinArtifact is [PersonOnly] and may only be invoked directly by the brain owner.");
        }

        var title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : State.ActiveArtifact?.Title ?? flowId;
        var alias = InoModel.SlugifyAlias(request.Alias ?? title, "pinned-app");

        try
        {
            var grain = CodeOrchestrations[ResolveCodeOrchestrationId(CurrentBrain, flowId)];
            var receipt = await grain.CrystallizeAsync(alias, title);
            var baseArtifact = State.ActiveArtifact ?? new InoDrawerArtifact($"code-orch:{flowId}", title, "code-orchestration", "done", FlowId: flowId);
            if (receipt.Succeeded)
            {
                var updated = baseArtifact with
                {
                    Title = string.IsNullOrWhiteSpace(receipt.Title) ? baseArtifact.Title : receipt.Title,
                    PinnedAppAlias = receipt.Alias,
                    LiveViewPath = string.IsNullOrWhiteSpace(receipt.LiveViewPath) ? baseArtifact.LiveViewPath : receipt.LiveViewPath,
                    ScopedContracts = receipt.ScopedContracts ?? baseArtifact.ScopedContracts,
                    PinError = null,
                    ExecutionTarget = receipt.ExecutionTarget ?? baseArtifact.ExecutionTarget,
                };
                await Commit(State.OpenArtifactDrawer(updated) with { Status = $"Pinned '{updated.Title}' as app '{receipt.Alias}'" });
            }
            else
            {
                await Commit(State with { ActiveArtifact = baseArtifact with { PinError = receipt.Error ?? "Could not pin app." } });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "code-orchestration-crystallize");
            if (State.ActiveArtifact is { } current)
            {
                await Commit(State with { ActiveArtifact = current with { PinError = error.Message } });
            }
        }
    }

    private static NeuronId ResolveCodeOrchestrationId(BrainId brain, string flowId)
        => NeuronId.TryParse(flowId, out var parsed)
            && parsed.Brain == brain
            && string.Equals(parsed.Type, "flow.code-orchestration", StringComparison.Ordinal)
            ? parsed
            : new NeuronId(brain, "flow.code-orchestration", flowId);

    [On]
    public async Task Link(InoPasteLink request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Request == Guid.Empty || !InoLinkCard.IsAppName(request.App)) { return; }
        var input = request.Input ?? string.Empty;
        if (input.Length > InoLinkCard.MaxInput) { throw new ArgumentException("The link input is too long."); }
        var id = request.Request.ToString("N");
        var records = Records;
        var kept = await records.Read();
        if (!kept.Links.Any(link => link.Request == id))
        {
            await records.Write(kept with { Links = [.. kept.Links.TakeLast(InoLinkCard.MaxLinksKept - 1), new InoLink(id, request.App!, input)] });
        }
        await Conversations[InoModel.ConversationKeyOf(State.ConversationId)]
            .Post(string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text, InoLinkCard.LinkPostId(request.Request));
    }

    [On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ExtractSendText(request) is not { } text)
        {
            var emptyCheck = InoModel.DecideSend(State, request.EffectiveMessage, "empty", NowUtc());
            if (emptyCheck.IsRefused) { await RecordRejectionAsync(emptyCheck.Rejection); }
            return;
        }
        var settings = await UserSettings.Read();
        if (text.StartsWith('/')) { await InoModelSetup.HandleSlashCommandAsync(Setup, text, settings, StartConversationAsync); return; }
        if (Compute is { } compute && await ReadOptionalAsync("compute", false, async () => (await compute.Read()).BudgetExceeded))
        {
            await Commit(InoModel.BudgetExceeded(State));
            return;
        }
        var decision = InoModel.DecideSend(State, text, Guid.NewGuid().ToString("N"), NowUtc());
        if (decision.IsRefused)
        {
            await RecordRejectionAsync(decision.Rejection);
            return;
        }
        if (decision.Next.Pending is not { } pending) { return; }
        await Commit(decision.Next with { DraftPrompt = null });
        await InoModelSetup.ExecuteTurnAsync(Setup, settings, pending);
    }

    [On]
    public async Task Retry(InoRetry request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RetryTurnAsync(null);
    }

    [On]
    public async Task UseLocalModel(InoUseLocalModel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model is not null || request.Value is not null)
        {
            var explicitModel = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : request.Value;
            if (Decision.Check("use-local", InoAppState.ValidModelRule(explicitModel)) is { } refused)
            {
                await RecordRejectionAsync(refused.Rejection);
                return;
            }
        }
        if (await InoModelSetup.ApplyLocalModelAsync(Setup, request)) { await Retry(new InoRetry()); }
    }

    [On]
    public async Task ChooseModel(InoChooseModel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("choose-model", InoAppState.ValidModelRule(request.Model)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }

        await RefreshPickerAsync();
        await InoModelSetup.ChooseModelAsync(Setup, request.Model!, request.MakeDefault, PickerAvailability, DeviceSnapshot);
        DeviceSnapshot = await InoModelSetup.ReadDeviceAsync(Setup);
        await Commit(State);
    }

    [On]
    public async Task OnInstallChanged(InstallChanged? changed)
    {
        if (changed is null) { return; }
        if (changed.State is InstallState.Ready or InstallState.Loaded or InstallState.Failed or InstallState.Cancelled)
        {
            var snapshot = await RefreshPickerAsync();
            Voice = await InoModelSetup.ReadVoiceAsync(Setup, snapshot.Catalog?.Availability);
        }
        else
        {
            DeviceSnapshot = await InoModelSetup.ReadDeviceAsync(Setup);
        }
        await Commit(State);
    }

    [On]
    public async Task SaveProvider(InoSaveProvider request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check(
                "save-provider",
                InoAppState.OptionalProviderRule(request.Provider),
                InoAppState.ValidCredentialRule(request.Value)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }
        var saved = await InoModelSetup.SaveProviderAsync(Setup, request);
        var snapshot = await RefreshPickerAsync();
        Voice = await InoModelSetup.ReadVoiceAsync(Setup, snapshot.Catalog?.Availability);
        if (saved) { await Retry(new InoRetry()); }
        if (State.HasUnsavedProviderDraft) { await Commit(State with { HasUnsavedProviderDraft = false }); }
    }

    [On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryOptionalAsync("conversation-stop", () => Conversations[InoModel.ConversationKeyOf(State.ConversationId)].Stop());
    }

    [On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = await UserSettings.SetVariable(InoModelSetup.SmartVariable, request.On ? "on" : "off");
        await Commit(State with { Smart = InoModelSetup.IsSmart(updated) });
    }

    [On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { var data = Data; var result = await data.Connect(request.ConnectionString ?? string.Empty); await Commit(InoModel.DecideConnectDb(State, (await data.Read()).ConnectedDatabase, result.Text, result.Card)); }
        catch (Exception error) when (error is not OperationCanceledException) { if (Logger is { } log) { InoLog.LogDatabaseFailed(log, error, "connect-db"); } await Commit(State with { Status = $"Could not connect: {error.Message}" }); }
    }

    [On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try
        {
            var brain = Context.Caller?.Brain ?? default;
            result = await IntoChat.OS.Apps.Data.WorkspaceDataset.Read(WorkspaceFiles, sql) is { } dataset
                ? DataAnalyticsEngine.ExecuteInMemory(sql, database, dataset.Text, dataset.Path, brain)
                : await DataAnalyticsEngine.ExecuteAsync(sql, database, brain: brain);
        }
        catch (Exception error) when (error is not OperationCanceledException) { if (Logger is { } log) { InoLog.LogDatabaseFailed(log, error, "query-db"); } await Commit(State with { Status = $"Could not run the query: {error.Message}" }); return; }
        if (Memory is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6)); }
        await Commit(InoModel.DecideQueryDbSuccess(State, result, database));
    }

    [On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (AppStudio is { } studio) { target = await ReadOptionalAsync("app-studio", target, async () => (await studio.Build(sentence)).Name); }
        if (Memory is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6)); }
        await Commit(InoModel.DecideMake(State, target, sentence, CurrentBrain.Value));
    }

    [On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null || (string.IsNullOrEmpty(changed.DisplayName) && string.IsNullOrEmpty(changed.DefaultModel))) { return; }
        var snapshot = await RefreshPickerAsync();
        Voice = await InoModelSetup.ReadVoiceAsync(Setup, snapshot.Catalog?.Availability);
        await InoModelSetup.SyncSettingsAsync(Setup, changed, snapshot);
    }

    private async Task RefreshConversationAsync(bool full)
    {
        var conversationId = State.ConversationId;
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        ConversationView? incoming;
        try
        {
            incoming = await Conversations[conversationKey].Read(full || ConversationView is null ? null : SeenRevision);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "conversation-read");
            return;
        }

        if (!string.Equals(incoming.Id.Value, State.ConversationId, StringComparison.Ordinal))
        {
            return;
        }

        if (!full && ConversationView is not null && incoming.Revision < SeenRevision)
        {
            return;
        }

        var merged = ConversationThreadProjection.MergeView(ConversationView, incoming, full);
        var hadRunningOrPending = State.ActiveRunId is not null || State.Pending is not null;
        ConversationView = merged;
        SeenRevision = merged.Revision;
        await MarkCurrentThreadReadAsync(merged.Revision);

        await LoadAttachmentsAsync(merged);

        var next = InoModel.FromConversation(State, merged);
        if (next.Error?.Code == "provider-unavailable" && await InoModelSetup.IsMissingLocalOllamaAsync(Setup, null))
        {
            next = next with { Error = new InoError("no-provider") };
        }

        if (merged.Active is null && hadRunningOrPending)
        {
            var lastCompleted = merged.Runs.LastOrDefault(static r => !r.Posted && r.Status == AgentRunStatus.Completed);
            if (lastCompleted is not null && !string.Equals(CompletedRunId, lastCompleted.RunId, StringComparison.Ordinal))
            {
                CompletedRunId = lastCompleted.RunId;
                var tokens = ComputeRunTokens(lastCompleted);
                if (Compute is { } compute) { await TryOptionalAsync("compute-record", () => compute.Record("ino", tokens)); }
                next = next with { Approvals = await InoModelSetup.ReadApprovalsAsync(Setup, next.Approvals) };
            }
        }

        if (InoThread.LatestCodeOrchestrationArtifact(merged) is { FlowId.Length: > 0 } latestArtifact)
        {
            var unseenFlow = !string.Equals(next.LastAutoOpenedFlowId, latestArtifact.FlowId, StringComparison.Ordinal);
            if (unseenFlow)
            {
                next = next.OpenArtifactDrawer(latestArtifact) with { LastAutoOpenedFlowId = latestArtifact.FlowId };
            }
            else if (next.ActiveArtifact is { } existing
                && string.Equals(existing.FlowId, latestArtifact.FlowId, StringComparison.Ordinal))
            {
                var updated = latestArtifact with
                {
                    PinnedAppAlias = existing.PinnedAppAlias ?? latestArtifact.PinnedAppAlias,
                    PinError = existing.PinError,
                };
                if (!existing.Equals(updated))
                {
                    next = next with { ActiveArtifact = updated };
                }
            }
        }

        await Commit(next);
    }

    private async Task LoadAttachmentsAsync(ConversationView view)
    {
        if (InoThread.HasLinkPosts(view))
        {
            await TryOptionalAsync("ino-links", async () =>
            {
                var records = await Records.Read();
                LinksJson = records.Links.Length == 0
                    ? null
                    : InoLinkCard.SerializeLinks(records.Links);
            });
        }

        var referenced = InoThread.ReferencedAppNames(view, LinksJson);
        if (referenced.Count == 0 || CurrentBrain.Value is not { Length: > 0 })
        {
            return;
        }

        Dictionary<string, AppManifest?>? updated = null;
        foreach (var appName in referenced)
        {
            if (AppManifests.ContainsKey(appName)) { continue; }
            AppManifest? manifest = null;
            await TryOptionalAsync("app-manifest", async () =>
            {
                var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(CurrentBrain, appName).Key;
                manifest = (await Apps[new NeuronId(CurrentBrain, AppGrainType.Name, appKey[(CurrentBrain.Value.Length + 1)..])].Read()).Manifest;
            });
            updated ??= new Dictionary<string, AppManifest?>(AppManifests, StringComparer.Ordinal);
            updated[appName] = manifest;
        }

        if (updated is not null)
        {
            AppManifests = updated;
        }
    }

    private static long ComputeRunTokens(AgentRun run)
    {
        if (run.Usage is { } usage)
        {
            var total = usage.TotalTokens ?? ((usage.InputTokens ?? 0L) + (usage.OutputTokens ?? 0L));
            if (total > 0) { return total; }
        }
        return Math.Max(1_000L, (run.Reply?.Length ?? 0) * 25L);
    }

    private void ResetConversationCache(string conversationId)
    {
        ConversationView = new ConversationView(ConversationId.Parse(conversationId), 0, [], [], null, [], false, []);
        SeenRevision = 0;
        SyncedThread = null;
        SyncedProps = null;
        CompletedRunId = null;
    }

    private async Task TrySeatFromSettingsAsync(
        string conversationId,
        UserSettingsState? cachedSettings = null,
        InoRuntimeSnapshot? snapshot = null)
    {
        var settings = cachedSettings ?? await ReadOptionalAsync<UserSettingsState?>("settings-read", null, async () => await UserSettings.Read());
        if (settings is null)
        {
            if (AgentAppPolicy.IsSpecialistConversationId(conversationId))
            {
                await InoModelSetup.EnsureSeatedAsync(Setup, conversationId, new UserSettingsState(), effectiveModel: null);
            }
            return;
        }
        var (_, effectiveModel, _) = await InoModelSetup.ResolveForSettingsAsync(Setup, settings, snapshot);
        await InoModelSetup.EnsureSeatedAsync(Setup, conversationId, settings, effectiveModel);
    }

    private async Task<InoRuntimeSnapshot> RefreshPickerAsync()
    {
        var catalog = await InoModelSetup.ReadCatalogSnapshotAsync(Setup);
        if (catalog is not null)
        {
            PickerCards = catalog.Cards;
            PickerAvailability = catalog.Availability;
        }
        var device = await InoModelSetup.ReadDeviceAsync(Setup);
        DeviceSnapshot = device;
        return new InoRuntimeSnapshot(catalog, device);
    }

    private IDevice? GetThisDevice()
        => ThisMachine is { Value.Length: > 0 } machine && CurrentBrain.Value is { Length: > 0 }
            ? Devices[new NeuronId(CurrentBrain, "devices.device", $"devices/{machine.Value}")]
            : null;

    private InoSetupHost Setup => SetupHost ??= new InoSetupHost(
        () => State,
        Commit,
        () => CurrentBrain,
        () => ModelCatalog,
        GetThisDevice,
        () => UserSettings,
        key => Conversations[key],
        () => SessionList,
        () => Grants,
        () => Approvals,
        () => ProfileWriter,
        Follow<IConversation, ConversationChanged>,
        RefreshConversationAsync,
        TryOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        ReadOptionalAsync,
        LogOptional,
        Logger,
        TryGetAppNeuron);

    private async Task StartConversationAsync()
    {
        var conversationId = InoModel.MintConversationId();
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        ResetConversationCache(conversationId);
        await InoModelSetup.OpenSessionAsync(Setup, conversationId);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        var snapshot = await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations);
        await Commit(InoModel.NewConversation(State, conversationId) with { RecentConversations = recents });
        await TrySeatFromSettingsAsync(conversationId, snapshot: snapshot);
        await RefreshConversationAsync(full: true);
    }

    private async Task SwitchConversationAsync(string? rawConversationId, bool allowSameThreadRefresh = false)
    {
        Rule[] rules = allowSameThreadRefresh
            ? [InoAppState.ValidThreadSelectionRule(rawConversationId)]
            : [InoAppState.ValidThreadSelectionRule(rawConversationId), State.DistinctThreadSelectionRule(rawConversationId)];
        if (Decision.Check("switch-conversation", rules) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }
        var conversationId = InoModel.ConversationIdOf(rawConversationId!);
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        ResetConversationCache(conversationId);

        string? specialistAppId = null;
        string? specialistHandle = null;
        string? specialistTitle = null;
        string? specialistDescription = null;
        string[] specialistStarters = [];
        if (AgentAppPolicy.TryAppIdOf(conversationId, out var parsedAppId) && TryGetAppNeuron(parsedAppId) is { } specialistApp)
        {
            specialistAppId = parsedAppId;
            AppSnapshot? appSnapshot = null;
            await TryOptionalAsync("specialist-app-read", async () => appSnapshot = await specialistApp.Read());
            if (appSnapshot?.Manifest is { Agent: not null } manifest)
            {
                specialistTitle = string.IsNullOrWhiteSpace(manifest.Title) ? parsedAppId : manifest.Title.Trim();
                specialistDescription = manifest.Description;
                specialistHandle = manifest.Agent.Handle;
                specialistStarters = manifest.Agent.Starters ?? [];
            }
        }

        await InoModelSetup.OpenSessionAsync(Setup, conversationId, specialistTitle);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        var snapshot = await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations);
        var baseSwitched = InoModel.SwitchConversation(State, conversationId, recents);
        var nextState = baseSwitched with
        {
            DraftPrompt = null,
            Title = specialistTitle ?? baseSwitched.Title,
            ActiveSpecialistAppId = specialistAppId,
            ActiveSpecialistHandle = specialistHandle,
            ActiveSpecialistDescription = specialistDescription,
            SpecialistStarters = specialistStarters,
            SpecialistPanelMode = null,
            SpecialistError = null,
        };
        await Commit(nextState);
        await TrySeatFromSettingsAsync(conversationId, snapshot: snapshot);
        await RefreshConversationAsync(full: true);
    }

    private async Task RefreshApprovalsAsync(InoAppState? baseState = null, bool skipWhenBothEmpty = false)
    {
        var approvals = await InoModelSetup.ReadApprovalsAsync(Setup, State.Approvals);
        var target = baseState ?? State;
        if (skipWhenBothEmpty && baseState is null && approvals.Length == 0 && target.Approvals.Length == 0) { return; }
        await Commit(target with { Approvals = approvals });
    }

    private static string? ExtractSendText(InoSend r) => string.IsNullOrWhiteSpace(r.EffectiveMessage) ? null : r.EffectiveMessage.Length > InoModel.MaxMessageChars ? r.EffectiveMessage : r.EffectiveMessage.Trim();
    private bool IsCurrentConversation(ConversationChanged? changed) => changed is not null && string.Equals(changed.Id.Value, State.ConversationId, StringComparison.Ordinal);
    private DateTimeOffset NowUtc() { if (Clock is not null) { return Clock.GetUtcNow(); } try { return Context.Time.GetUtcNow(); } catch (InvalidOperationException error) { LogOptional(error, "clock"); return TimeProvider.System.GetUtcNow(); } }
    private BrainId CurrentBrain { get { try { return Id.Brain; } catch (InvalidOperationException error) { LogOptional(error, "brain-id"); return default; } } }
    private async Task TryOptionalAsync(string target, Func<Task> action) { try { await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); } }
    private async Task<T> ReadOptionalAsync<T>(string target, T fallback, Func<Task<T>> action) { try { return await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); return fallback; } }
    private void LogOptional(Exception error, string target) { if (Logger is { } log) { InoLog.LogOptionalUnavailable(log, error, target); } }
}
