/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, choose-model, save-provider, connect-db, query-db, new, new-conversation, switch-conversation, chats, toggle-devui, make, link, ui.command
*/
using IntoChat.OS.Host.Apps;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.UI.Chat.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

[Neuron("app.ino")]
[Screen("app.ino", Title = "Ino", Icon = Icons.Sparkles)]
public sealed partial class InoApp : AggregateScreen<InoAppState>, IWithFilesVfs, INavigatedTo<InoNavArgs>, INavigatedFrom
{
    private const string ApprovalsKey = "approvals/index", DataKey = "ino/data", ModelsKey = "main";

    [Slot(Key = "settings")] private partial IUserSettings UserSettings { get; }
    [Slot(Key = IInoRecords.Key)] private partial IInoRecords Records { get; }
    [Slot(Key = "compute", Required = false)] private partial ICompute? Compute { get; }
    [Slot(Key = DataKey)] private partial IData Data { get; }
    [Slot(Key = "memory", Required = false)] private partial IMemory? Memory { get; }
    [Slot(Key = "main", Required = false)] private partial IAppStudio? AppStudio { get; }
    [Slot(Key = ModelsKey)] private partial IModelCatalog ModelCatalog { get; }
    [Slot(Key = ISessionList.DefaultKey)] private partial ISessionList SessionList { get; }
    [Slot(Key = IGrantsNeuron.DefaultKey)] private partial IGrantsNeuron Grants { get; }
    [Slot(Key = ApprovalsKey)] private partial IApprovals Approvals { get; }
    [Slot(Key = "ino/profile-writer")] private partial IProfileContextWriter ProfileWriter { get; }
    [Slot] private partial SlotCollection<IConversation> Conversations { get; }
    [Slot(Required = false)] private partial SlotCollection<IApp> Apps { get; }
    [Slot(Required = false)] private partial SlotCollection<IDevice> Devices { get; }

    private ILogger<InoApp>? Logger { get; }
    private TimeProvider? Clock { get; }
    private MachineId? ThisMachine { get; }
    private InoSetupHost? SetupHost { get; set; }
    private ConversationView? ConversationView { get; set; }
    private long SeenRevision { get; set; }
    private SessionPage? SessionsPage { get; set; }
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
    public IFilesVfsService? FilesVfs { get; set; }
    public const string AppName = "ino";
    public static AppBundle Bundle { get; } = InoModelSetup.BuildBundle(typeof(InoApp));

    public new InoAppState State => base.State;
    public new Rejected? LastRejection => RecordedRejection ?? base.LastRejection;
    public View Render() => PreviewView(State);

    internal ConversationView? CurrentView => ConversationView;
    internal SessionPage? CurrentSessions => SessionsPage;
    internal IReadOnlyDictionary<string, AppManifest?> CurrentApps => AppManifests;
    internal string? CurrentLinks => LinksJson;

    protected override Node Render(InoAppState state)
        => InoView.ScreenNode(
            state,
            Voice,
            props => Child<ChatThreadNeuron, ChatThreadProps>(InoView.ConversationKey, props),
            ConversationView,
            SessionsPage,
            ThreadFailureText,
            base.LastRejection,
            PickerCards,
            PickerAvailability,
            DeviceSnapshot);

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
                ResetConversationCache(targetConversationId);
                if (attached)
                {
                    await InoModelSetup.OpenSessionAsync(Setup, targetConversationId);
                    await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(InoModel.ConversationKeyOf(targetConversationId)));
                }

                var recents = attached
                    ? await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations)
                    : State.RecentConversations;
                var switched = (InoModel.SwitchConversation(State, targetConversationId, recents) with { DraftPrompt = null })
                    .ApplyNavigation(args);
                if (attached)
                {
                    await Commit(switched);
                    await RefreshConversationAsync(full: true);
                }

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
            await FollowChild<ChatThreadNeuron, RetryRequested>(InoView.ConversationKey);
            await FollowChild<ChatThreadNeuron, Submitted>(InoView.ConversationKey);
            await FollowChild<ChatThreadNeuron, StopRequested>(InoView.ConversationKey);
            await FollowChild<ChatThreadNeuron, ChatModelSelected>(InoView.ConversationKey);
        });
        await TryOptionalAsync("settings", () => Follow<IUserSettings, UserSettingsChanged>("settings"));
        if (ThisMachine is { Value.Length: > 0 } machine)
        {
            await TryOptionalAsync("device-installs", () => Follow<IDevice, InstallChanged>($"devices/{machine.Value}"));
        }
        await TryOptionalAsync("confirmations-synapses", () => Context.Wires.EnsureBrainWide([typeof(ConfirmationRequested), typeof(ConfirmationResolved), typeof(FlowsChanged)]));
        var snapshot = await RefreshPickerAsync();
        var settings = await ReadOptionalAsync<UserSettingsState?>("settings-read", null, async () => await UserSettings.Read());
        Voice = await InoModelSetup.InitializeViewAsync(Setup, snapshot, settings);
        await InoModelSetup.OpenSessionAsync(Setup, State.ConversationId);
        var conversationKey = InoModel.ConversationKeyOf(State.ConversationId);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        await TrySeatFromSettingsAsync(State.ConversationId, settings, snapshot);
        await RefreshConversationAsync(full: true);
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
            SessionsPage = await InoModelSetup.ReadSessionsAsync(Setup) ?? new SessionPage([], 0);
        }
        await Commit(State with { ActiveSection = opening ? "chats" : "chat" });
    }

    [On] public Task ToggleDevUi(InoToggleDevUi request) { ArgumentNullException.ThrowIfNull(request); return Commit(State.ToggleDevUi()); }
    [On] public async Task OnGrantsChanged(GrantsChanged? changed) { if (changed is not null) { await Commit(State with { GrantSummaries = await InoModelSetup.ReadGrantSummariesAsync(Setup, State.GrantSummaries) }); } }
    [On] public Task New(InoNew request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task NewConversation(InoNewConversation request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task SwitchConversation(InoSwitchConversation request) { ArgumentNullException.ThrowIfNull(request); return SwitchConversationAsync(request.ConversationId); }
    [On] public Task OnConfirmationRequested(ConfirmationRequested? requested) => requested is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnConfirmationResolved(ConfirmationResolved? resolved) => resolved is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnFlowsChanged(FlowsChanged? changed) => changed is { FlowId.Length: > 0 } ? RefreshApprovalsAsync(skipWhenBothEmpty: true) : Task.CompletedTask;

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
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs, Context.Caller?.Brain ?? default); }
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

        var merged = MergeView(ConversationView, incoming, full);
        var hadRunningOrPending = State.ActiveRunId is not null || State.Pending is not null;
        ConversationView = merged;
        SeenRevision = merged.Revision;

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

    private static ConversationView MergeView(ConversationView? previous, ConversationView incoming, bool full)
    {
        if (full || previous is null || !string.Equals(previous.Id.Value, incoming.Id.Value, StringComparison.Ordinal))
        {
            return incoming;
        }

        if (incoming.Entries.Length == 0)
        {
            return incoming with { Entries = previous.Entries };
        }

        var mergedEntries = new List<ConversationEntry>(previous.Entries.Length + incoming.Entries.Length);
        mergedEntries.AddRange(previous.Entries);
        var seen = new HashSet<EntryId>(previous.Entries.Select(static e => e.Id));
        foreach (var entry in incoming.Entries)
        {
            if (seen.Add(entry.Id))
            {
                mergedEntries.Add(entry);
            }
        }
        return incoming with { Entries = [.. mergedEntries] };
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
        if (settings is null) { return; }
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
        Logger);

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

    private async Task SwitchConversationAsync(string? rawConversationId)
    {
        if (Decision.Check(
                "switch-conversation",
                InoAppState.ValidThreadSelectionRule(rawConversationId),
                State.DistinctThreadSelectionRule(rawConversationId)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }
        var conversationId = InoModel.ConversationIdOf(rawConversationId!);
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        ResetConversationCache(conversationId);
        await InoModelSetup.OpenSessionAsync(Setup, conversationId);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        var snapshot = await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations);
        await Commit(InoModel.SwitchConversation(State, conversationId, recents) with { DraftPrompt = null });
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
