/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, choose-model, save-provider, connect-db, query-db, new, new-conversation, switch-conversation, toggle-devui, make, link, ui.command
*/
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.Chat.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Apps.Ino;

[Neuron("app.ino")]
[Screen("app.ino", Title = "Ino", Icon = Icons.Sparkles)]
public sealed class InoApp : AggregateScreen<InoAppState>, IWithFilesVfs, INavigatedTo<InoNavArgs>, INavigatedFrom
{
    private const string ApprovalsKey = "approvals/index", DataKey = "ino/data", ModelsKey = "main";

    private ILogger<InoApp>? Logger { get; }
    private TimeProvider? Clock { get; }
    private MachineId? ThisMachine { get; }
    private InoSetupHost? SetupHost { get; set; }
    private InoMessage[]? HeldMessages { get; set; }
    private IReadOnlyList<ThreadMessage>? SyncedThread { get; set; }
    private bool ThreadKnown { get; set; }
    private ChatThreadProps? SyncedProps { get; set; }
    private ModelCard[]? PickerCards { get; set; }
    private ModelAvailability[]? PickerAvailability { get; set; }
    private DeviceView? DeviceSnapshot { get; set; }
    private string? ThreadFailureText { get; set; }
    private Rejected? RecordedRejection { get; set; }

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

    public bool CanDictate { get; private set; }
    public IFilesVfsService? FilesVfs { get; set; }
    public const string AppName = "ino";
    public static AppBundle Bundle { get; } = InoModelSetup.BuildBundle(typeof(InoApp));

    public new InoAppState State => base.State;
    public new Rejected? LastRejection => RecordedRejection ?? base.LastRejection;
    public View Render() => PreviewView(Current);
    public View RenderSide() => InoView.Side(Current);

    /// <summary>The messages Ino last projected from its chat; the conversation thread holds them (the stored state never does).</summary>
    public IReadOnlyList<InoMessage> Messages => Current.Messages ?? [];

    protected override Node Render(InoAppState state)
        => InoView.ScreenNode(
            state.Messages is null && HeldMessages is { } held ? state with { Messages = held } : state,
            CanDictate,
            props => Slot<ChatThreadNeuron, ChatThreadProps>(InoView.ConversationKey, props),
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
            await Commit(Current with { HasUnsavedProviderDraft = false });
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
            if (!string.Equals(targetConversationId, Current.ConversationId, StringComparison.Ordinal))
            {
                var agentKey = InoModel.AgentKeyOf(targetConversationId);
                ThreadKnown = false;
                SyncedThread = null;
                SyncedProps = null;
                if (attached)
                {
                    await InoModelSetup.FollowAgentControlSignalsAsync(Setup, agentKey);
                }

                var recents = attached
                    ? await InoModelSetup.ReadRecentConversationsAsync(Setup, Current.RecentConversations)
                    : Current.RecentConversations;
                var switched = (InoModel.SwitchConversation(Current, targetConversationId, recents) with { DraftPrompt = null })
                    .ApplyNavigation(args);
                if (attached)
                {
                    await Commit(switched);
                    await TryOptionalAsync("ui-chat-synapses", () => FollowUiChatSignalsAsync(InoModel.UiChatKeyOf(targetConversationId)));
                    if (await InoModelSetup.EnsureChatAsync(Setup, agentKey))
                    {
                        await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey);
                    }
                }

                return;
            }
        }

        var next = Current.ApplyNavigation(args);
        if (attached)
        {
            await Commit(next);
        }
    }

    // Model decisions read the messages last projected; until a projection after activation, the chat revision reads 0 so the next
    // projection runs even when the chat has not changed since the messages were last projected.
    private InoAppState Current => State.Messages is not null
        ? State
        : HeldMessages is { } held
            ? State with { Messages = held }
            : State with { ChatRevision = 0 };

    private Task RecordRejectionAsync(Rejected rejection)
    {
        RecordedRejection = rejection;
        return PublishEphemeral(rejection);
    }

    // Saves Ino's state without its messages and writes them to the conversation thread: the whole thread once the messages are known
    // (projected, a new conversation, or migrated), otherwise only what is new (a pending prompt) so a thread from before activation stays.
    private async Task Commit(InoAppState next)
    {
        RecordedRejection = null;
        HeldMessages = next.Messages;
        ThreadKnown |= next.Messages is not null;
        await Show(next with { Messages = null });
        await InoModelSetup.SyncUiChatThreadAsync(Setup, next, CanDictate);
        var thread = InoView.Thread(next);
        if (!ThreadKnown && thread.Count == 0) { return; }
        // The thread draws its stop and the error's retry from the props routed with the import: a props change writes it again too.
        var props = InoView.ThreadProps(next, CanDictate, ThreadFailureText, PickerCards, PickerAvailability, DeviceSnapshot);
        if (SyncedThread is { } synced && synced.SequenceEqual(thread) && Equals(props, SyncedProps)) { return; }
        // Ino's own save stands when the thread cannot be written: the slot's props say so (to the person and to MCP readers), and the
        // next save writes the thread again.
        try
        {
            await SendToThread(new ImportMessages(thread, Replace: ThreadKnown));
            SyncedThread = thread;
            SyncedProps = props;
            if (ThreadFailureText is not null) { ThreadFailureText = null; await Show(next with { Messages = null }); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SyncedThread = null;
            SyncedProps = null;
            LogOptional(error, "conversation-thread");
            if (ThreadFailureText is null) { ThreadFailureText = ThreadFailure; await Show(next with { Messages = null }); }
        }
    }

    private Task SendToThread(ImportMessages command)
        => Send(new Command(InoView.ConversationKey, "control.importMessages",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(command, typeof(ImportMessages))));

    // Messages stored by Ino before the conversation thread held them: imported once (the import is idempotent by id), then cleared in one save.
    private async Task MigrateMessagesAsync()
    {
        if (State.Messages is not { } legacy) { return; }
        // Cleared only once the thread holds them: a failed import is tried again on the next activation.
        try { if (legacy.Length > 0) { await SendToThread(new ImportMessages(InoView.Thread(State), Replace: false)); } }
        catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, "conversation-thread-migration"); return; }
        HeldMessages = legacy;
        ThreadKnown = true;
        SyncedThread = null;
        await Show(State with { Messages = null });
    }

    [On]
    public Task OnThreadRetry(RetryRequested? requested)
        => requested is not null && SignalSlot == InoView.ConversationKey ? RetryTurnAsync(requested.Id) : Task.CompletedTask;

    [On]
    public Task OnThreadSubmitted(Submitted? submitted)
        => submitted is not null && SignalSlot == InoView.ConversationKey && submitted.Values.TryGetValue("text", out var text)
            ? Send(new InoSend(Message: text)) : Task.CompletedTask;

    [On]
    public Task OnThreadStop(StopRequested? requested)
        => requested is not null && SignalSlot == InoView.ConversationKey ? Stop(new InoStop()) : Task.CompletedTask;

    [On]
    public Task OnUiChatSubmitted(ChatMessageSubmitted? submitted)
        => submitted is not null && IsCurrentUiChat(submitted.ChatId) ? Send(new InoSend(Message: submitted.Text)) : Task.CompletedTask;

    [On]
    public Task OnUiChatStop(ChatStopRequested? requested)
        => requested is not null && IsCurrentUiChat(requested.ChatId) ? Stop(new InoStop()) : Task.CompletedTask;

    [On]
    public Task OnUiChatRetry(ChatRetryRequested? requested)
        => requested is not null && IsCurrentUiChat(requested.ChatId) ? RetryTurnAsync(requested.MessageId) : Task.CompletedTask;

    [On]
    public Task OnUiChatCompact(ChatCompactRequested? requested)
        => requested is not null && IsCurrentUiChat(requested.ChatId) ? Send(new InoSend(Message: "/compact")) : Task.CompletedTask;

    [On]
    public Task OnUiChatModelSelected(ChatModelSelected? selected)
        => selected is not null && (SignalSlot == InoView.ConversationKey || IsCurrentUiChat(selected.ChatId))
            ? ChooseModel(new InoChooseModel(Model: selected.ModelId, MakeDefault: selected.MakeDefault))
            : Task.CompletedTask;

    // Sends a turn again: an empty id is the last prompt; a user message's id is that prompt; a reply's id is the prompt it answered.
    private async Task RetryTurnAsync(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            var retryDecision = InoModel.DecideRetryPrompt(Current);
            if (retryDecision.IsRefused)
            {
                await RecordRejectionAsync(retryDecision.Rejection);
                return;
            }
            await Send(new InoSend(Message: retryDecision.Next));
            return;
        }
        if (InoModel.PromptOf(Current, id) is { } prompt) { await Send(new InoSend(Message: prompt)); }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await MigrateMessagesAsync();
        await TryOptionalAsync("conversation-thread", async () =>
        {
            await FollowSlot<ChatThreadNeuron, RetryRequested>(InoView.ConversationKey);
            await FollowSlot<ChatThreadNeuron, Submitted>(InoView.ConversationKey);
            await FollowSlot<ChatThreadNeuron, StopRequested>(InoView.ConversationKey);
            await FollowSlot<ChatThreadNeuron, ChatModelSelected>(InoView.ConversationKey);
        });
        await TryOptionalAsync("settings", () => Follow<IUserSettings, UserSettingsChanged>("settings"));
        if (ThisMachine is { Value.Length: > 0 } machine)
        {
            await TryOptionalAsync("device-installs", () => Follow<IDevice, InstallChanged>($"devices/{machine.Value}"));
        }
        await TryOptionalAsync("confirmations-synapses", () => Context.Wires.EnsureBrainWide([typeof(ConfirmationRequested), typeof(ConfirmationResolved), typeof(FlowsChanged)]));
        await RefreshPickerAsync();
        CanDictate = await InoModelSetup.InitializeViewAsync(Setup);
        var agentKey = InoModel.AgentKeyOf(Current.ConversationId);
        await TryOptionalAsync("conversations-registry", () => Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey));
        await TryOptionalAsync("ui-chat-synapses", () => FollowUiChatSignalsAsync(InoModel.UiChatKeyOf(Current.ConversationId)));
        if (await InoModelSetup.EnsureChatAsync(Setup, agentKey)) { await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey); }
    }

    [On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var (device, effectiveModel, displayModel) = await InoModelSetup.ResolveForSettingsAsync(Setup, settings);
        DeviceSnapshot = device;
        CanDictate = await InoModelSetup.ReadCanDictateAsync(Setup);
        var (nextState, migrate) = InoModel.DecideOpen(
            Current, request.Mode, settings.DisplayName, displayModel, InoModelSetup.IsSmart(settings),
            await InoModelSetup.ReadApprovalsAsync(Setup, Current.Approvals), await InoModelSetup.ReadGrantSummariesAsync(Setup, Current.GrantSummaries));

        var agentKey = InoModel.AgentKeyOf(Current.ConversationId);
        await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, Current.RecentConversations);
        var devUi = string.Equals(request.Mode, "devui", StringComparison.OrdinalIgnoreCase) || Current.DevUiMode;
        await Commit(nextState with { RecentConversations = recents, DevUiMode = devUi });
        await TryOptionalAsync("agent", () => InoModelSetup.ConfigureOpenAgentAsync(Setup, agentKey, settings, effectiveModel, migrate));
        if (await InoModelSetup.EnsureChatAsync(Setup, agentKey)) { await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey); }
    }

    [On] public Task ToggleDevUi(InoToggleDevUi request) { ArgumentNullException.ThrowIfNull(request); return Commit(Current.ToggleDevUi()); }

    [On] public async Task OnGrantsChanged(GrantsChanged? changed) { if (changed is not null) { await Commit(Current with { GrantSummaries = await InoModelSetup.ReadGrantSummariesAsync(Setup, Current.GrantSummaries) }); } }
    [On] public Task New(InoNew request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task NewConversation(InoNewConversation request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task SwitchConversation(InoSwitchConversation request) { ArgumentNullException.ThrowIfNull(request); return SwitchConversationAsync(request.ConversationId); }
    [On] public Task OnConfirmationRequested(ConfirmationRequested? requested) => requested is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnConfirmationResolved(ConfirmationResolved? resolved) => resolved is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnFlowsChanged(FlowsChanged? changed) => changed is { FlowId.Length: > 0 } ? RefreshApprovalsAsync(skipWhenBothEmpty: true) : Task.CompletedTask;
    [On] public Task OnChatChanged(ChatChanged? changed) => IsCurrentChat(changed?.Name) ? InoModelSetup.ProjectCurrentChatAsync(Setup, InoModel.AgentKeyOf(Current.ConversationId)) : Task.CompletedTask;

    [On]
    public async Task Link(InoPasteLink request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Request == Guid.Empty || !ChatAppCard.IsAppName(request.App)) { return; }
        var input = request.Input ?? string.Empty;
        if (input.Length > ChatAppCard.MaxInput) { throw new ArgumentException("The link input is too long."); }
        var id = request.Request.ToString("N");
        var records = Get<IInoRecords>(IInoRecords.Key);
        var kept = await records.Read();
        if (!kept.Links.Any(link => link.Request == id))
        { await records.Write(kept with { Links = [.. kept.Links.TakeLast(ChatAppCard.MaxLinksKept - 1), new InoLink(id, request.App!, input)] }); }
        await Get<IAgent>(InoModel.AgentKeyOf(Current.ConversationId)).Post(string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text, ChatAppCard.LinkPostId(request.Request));
    }

    [On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ExtractSendText(request) is not { } text)
        {
            var emptyCheck = InoModel.DecideSend(Current, request.EffectiveMessage, "empty", NowUtc());
            if (emptyCheck.IsRefused) { await RecordRejectionAsync(emptyCheck.Rejection); }
            return;
        }
        var settings = await Get<IUserSettings>("settings").Read();
        if (text.StartsWith('/')) { await InoModelSetup.HandleSlashCommandAsync(Setup, text, settings, StartConversationAsync); return; }
        if (await ReadOptionalAsync("compute", false, async () => (await Get<ICompute>("compute").Read()).BudgetExceeded))
        {
            await Commit(InoModel.BudgetExceeded(Current));
            return;
        }
        var decision = InoModel.DecideSend(Current, text, Guid.NewGuid().ToString("N"), NowUtc());
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
        await Commit(Current);
    }

    [On]
    public async Task OnInstallChanged(InstallChanged? changed)
    {
        if (changed is null) { return; }
        DeviceSnapshot = await InoModelSetup.ReadDeviceAsync(Setup);
        if (changed.State is InstallState.Ready or InstallState.Loaded or InstallState.Failed or InstallState.Cancelled)
        {
            await RefreshPickerAsync();
        }
        await Commit(Current);
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
        await RefreshPickerAsync();
        if (saved) { await Retry(new InoRetry()); }
        if (State.HasUnsavedProviderDraft) { await Commit(Current with { HasUnsavedProviderDraft = false }); }
    }

    [On]
    public async Task OnChatRunChanged(ChatRunChanged? changed)
    {
        if (!IsCurrentChat(changed?.Name)) { return; }
        await InoModelSetup.ProjectCurrentChatAsync(Setup, InoModel.AgentKeyOf(Current.ConversationId));
        if (changed!.Status == ChatRunStatus.Completed)
        {
            var fallbackTokens = Math.Max(1_000L, ((Current.Messages ?? []).LastOrDefault(m => m.Role == "assistant")?.Text?.Length ?? 0) * 25L);
            var actualTokens = await InoModelSetup.ReadLastRunTokensAsync(Setup, InoModel.AgentKeyOf(Current.ConversationId), fallbackTokens);
            await TryOptionalAsync("compute-record", () => Get<ICompute>("compute").Record("ino", actualTokens));
            await RefreshApprovalsAsync();
        }
    }

    [On]
    public async Task OnAgentBlocked(AgentBlocked? blocked)
    {
        if (blocked is null || string.IsNullOrEmpty(blocked.Agent) || !IsCurrentAgent(blocked.Agent)) { return; }
        await RefreshApprovalsAsync(blocked.Blocked ? InoModel.Pause(Current, "An interrupt holds the agent.") : InoModel.Resume(Current));
    }

    [On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryOptionalAsync("agent", async () => { var agent = Get<IAgent>(InoModel.AgentKeyOf(Current.ConversationId)); if (Current.ActiveRunId is not null || (await agent.Read()).ActiveRun is not null) { await agent.Cancel(); } });
    }

    [On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = await Get<IUserSettings>("settings").SetVariable(InoModelSetup.SmartVariable, request.On ? "on" : "off");
        await Commit(Current with { Smart = InoModelSetup.IsSmart(updated) });
    }

    [On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { var data = Get<IData>(DataKey); var result = await data.Connect(request.ConnectionString ?? string.Empty); await Commit(InoModel.DecideConnectDb(Current, (await data.Read()).ConnectedDatabase, result.Text, result.Card)); }
        catch (Exception error) when (error is not OperationCanceledException) { if (Logger is { } log) { InoLog.LogDatabaseFailed(log, error, "connect-db"); } await Commit(Current with { Status = $"Could not connect: {error.Message}" }); }
    }

    [On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(Current.ConnectedDatabase) ? InoDataCards.DefaultDatabase : Current.ConnectedDatabase;
        DataQueryResult result;
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs, Context.Caller?.Brain ?? default); }
        catch (Exception error) when (error is not OperationCanceledException) { if (Logger is { } log) { InoLog.LogDatabaseFailed(log, error, "query-db"); } await Commit(Current with { Status = $"Could not run the query: {error.Message}" }); return; }
        if (TryGetOptional<IMemory>("memory") is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6)); }
        await Commit(InoModel.DecideQueryDbSuccess(Current, result, database));
    }

    [On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetOptional<IAppStudio>("main") is { } studio) { target = await ReadOptionalAsync("app-studio", target, async () => (await studio.Build(sentence)).Name); }
        if (TryGetOptional<IMemory>("memory") is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6)); }
        await Commit(InoModel.DecideMake(Current, target, sentence, CurrentBrain.Value));
    }

    [On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null || (string.IsNullOrEmpty(changed.DisplayName) && string.IsNullOrEmpty(changed.DefaultModel))) { return; }
        await RefreshPickerAsync();
        await InoModelSetup.SyncSettingsAsync(Setup, changed);
    }

    private async Task RefreshPickerAsync()
    {
        if (await InoModelSetup.ReadPickerAsync(Setup) is { } picker)
        {
            PickerCards = picker.Cards;
            PickerAvailability = picker.Availability;
        }
        DeviceSnapshot = await InoModelSetup.ReadDeviceAsync(Setup);
    }

    private IDevice? GetThisDevice()
        => ThisMachine is { Value.Length: > 0 } machine && CurrentBrain.Value is { Length: > 0 }
            ? Get<IDevice>(new NeuronId(CurrentBrain, "devices.device", $"devices/{machine.Value}"))
            : null;

    private InoSetupHost Setup => SetupHost ??= new InoSetupHost(
        () => Current, Commit, () => CurrentBrain,
        () => Get<IModelCatalog>(new NeuronId(CurrentBrain, "ai.models", ModelsKey)),
        GetThisDevice,
        () => Get<IUserSettings>("settings"), Get<IAgent>, Get<IChat>, Get<IToneObserver>,
        () => Get<ISpeechRecognizer>("default"), () => Get<IGrantsNeuron>(IGrantsNeuron.DefaultKey), () => Get<IApprovals>(ApprovalsKey),
        () => Get<IInoConversations>(IInoConversations.DefaultKey), () => Get<IProfileContextWriter>("ino/profile-writer"),
        Get<IAgentContext>, Get<IChatThread>,
        Follow<IAgent, AgentBlocked>,
        async chatKey => { await Follow<IChat, ChatChanged>(chatKey); await Follow<IChat, ChatRunChanged>(chatKey); },
        FollowUiChatSignalsAsync,
        TryOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync,
        LogOptional, Logger);

    private async Task FollowUiChatSignalsAsync(string uiChatKey)
    {
        await Follow<IChatThread, ChatMessageSubmitted>(uiChatKey);
        await Follow<IChatThread, ChatStopRequested>(uiChatKey);
        await Follow<IChatThread, ChatRetryRequested>(uiChatKey);
        await Follow<IChatThread, ChatCompactRequested>(uiChatKey);
        await Follow<IChatThread, ChatModelSelected>(uiChatKey);
    }

    private async Task StartConversationAsync()
    {
        var conversationId = InoModel.MintConversationId();
        var agentKey = InoModel.AgentKeyOf(conversationId);
        ThreadKnown = true;
        await InoModelSetup.FollowAgentControlSignalsAsync(Setup, agentKey);
        await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, Current.RecentConversations);
        await Commit(InoModel.NewConversation(Current, conversationId) with { RecentConversations = recents });
        await TryOptionalAsync("ui-chat-synapses", () => FollowUiChatSignalsAsync(InoModel.UiChatKeyOf(conversationId)));
        await InoModelSetup.EnsureChatAsync(Setup, agentKey);
    }

    private async Task SwitchConversationAsync(string? rawConversationId)
    {
        if (Decision.Check(
                "switch-conversation",
                InoAppState.ValidThreadSelectionRule(rawConversationId),
                Current.DistinctThreadSelectionRule(rawConversationId)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }
        var conversationId = InoModel.ConversationIdOf(rawConversationId!);
        var agentKey = InoModel.AgentKeyOf(conversationId);
        ThreadKnown = false;
        SyncedThread = null;
        SyncedProps = null;
        await InoModelSetup.FollowAgentControlSignalsAsync(Setup, agentKey);
        await RefreshPickerAsync();
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, Current.RecentConversations);
        await Commit(InoModel.SwitchConversation(Current, conversationId, recents) with { DraftPrompt = null });
        await TryOptionalAsync("ui-chat-synapses", () => FollowUiChatSignalsAsync(InoModel.UiChatKeyOf(conversationId)));
        if (await InoModelSetup.EnsureChatAsync(Setup, agentKey)) { await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey); }
    }

    private async Task RefreshApprovalsAsync(InoAppState? baseState = null, bool skipWhenBothEmpty = false)
    {
        var approvals = await InoModelSetup.ReadApprovalsAsync(Setup, Current.Approvals);
        var target = baseState ?? Current;
        if (skipWhenBothEmpty && baseState is null && approvals.Length == 0 && target.Approvals.Length == 0) { return; }
        await Commit(target with { Approvals = approvals });
    }

    private static string? ExtractSendText(InoSend r) => string.IsNullOrWhiteSpace(r.EffectiveMessage) ? null : r.EffectiveMessage.Length > InoModel.MaxMessageChars ? r.EffectiveMessage : r.EffectiveMessage.Trim();
    private bool IsCurrentAgent(string agentId) => string.Equals(agentId, InoModel.AgentKeyOf(Current.ConversationId), StringComparison.Ordinal);
    private bool IsCurrentChat(string? chatName) => !string.IsNullOrEmpty(chatName) && CurrentBrain.Value is { Length: > 0 } && string.Equals(chatName, new NeuronId(CurrentBrain, IChat.Type, InoModel.ChatKeyOf(Current.ConversationId)).FullKey(), StringComparison.Ordinal);
    private bool IsCurrentUiChat(string? chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId)) { return false; }
        var expected = InoModel.UiChatKeyOf(Current.ConversationId);
        return string.Equals(chatId, expected, StringComparison.Ordinal)
            || (CurrentBrain.Value is { Length: > 0 } && string.Equals(chatId, new NeuronId(CurrentBrain, "ui.chat", expected).FullKey(), StringComparison.Ordinal));
    }
    private DateTimeOffset NowUtc() { if (Clock is not null) { return Clock.GetUtcNow(); } try { return Context.Time.GetUtcNow(); } catch (InvalidOperationException error) { LogOptional(error, "clock"); return TimeProvider.System.GetUtcNow(); } }
    private BrainId CurrentBrain { get { try { return Id.Brain; } catch (InvalidOperationException error) { LogOptional(error, "brain-id"); return default; } } }
    private T? TryGetOptional<T>(string key) where T : class { try { return Get<T>(key); } catch (InvalidOperationException error) { LogOptional(error, typeof(T).Name); return null; } }
    private async Task TryOptionalAsync(string target, Func<Task> action) { try { await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); } }
    private async Task<T> ReadOptionalAsync<T>(string target, T fallback, Func<Task<T>> action) { try { return await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); return fallback; } }
    private void LogOptional(Exception error, string target) { if (Logger is { } log) { InoLog.LogOptionalUnavailable(log, error, target); } }
}
