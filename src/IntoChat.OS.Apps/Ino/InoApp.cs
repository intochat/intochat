/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, save-provider, connect-db, query-db, new, new-conversation, make, link
*/
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Compute;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

[Neuron("app.ino")]
public sealed class InoApp : Screen<InoAppState>, IWithFilesVfs
{
    private const string ApprovalsKey = "approvals/index", DataKey = "ino/data", ModelsKey = "main";
    private readonly ILogger<InoApp>? _logger;
    private readonly TimeProvider? _time;
    private InoSetupHost? _setupHost;
    private bool _canDictate;

    public InoApp() : this(null, null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public InoApp(ILogger<InoApp>? logger = null, TimeProvider? time = null) { _logger = logger; _time = time; }

    public bool CanDictate => _canDictate;
    public IFilesVfsService? FilesVfs { get; set; }
    public const string AppName = "ino";
    public static AppBundle Bundle { get; } = InoModelSetup.BuildBundle(typeof(InoApp));

    public new InoAppState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => InoView.Side(State);
    protected override View Render(InoAppState state) => InoView.Screen(state, _canDictate);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await TryOptionalAsync("settings", () => Follow<IUserSettings, UserSettingsChanged>("settings"));
        await TryOptionalAsync("confirmations-synapses", () => Context.Synapses.EnsureBrainWide([typeof(ConfirmationRequested), typeof(ConfirmationResolved), typeof(FlowsChanged)]));
        _canDictate = await InoModelSetup.InitializeViewAsync(Setup);
        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        await TryOptionalAsync("conversations-registry", () => Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey));
        if (await InoModelSetup.EnsureChatAsync(Setup, agentKey)) { await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey); }
    }

    [On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var (local, effectiveModel, displayModel) = await InoModelSetup.ResolveForSettingsAsync(Setup, settings);
        _canDictate = await InoModelSetup.ReadCanDictateAsync(Setup);
        var (nextState, migrate) = InoModel.DecideOpen(
            State, request.Mode, settings.DisplayName, displayModel, InoModelSetup.IsSmart(settings),
            await InoModelSetup.ReadApprovalsAsync(Setup, State.Approvals), await InoModelSetup.ReadGrantSummariesAsync(Setup, State.GrantSummaries),
            local?.PreferredModel, local?.InstalledModels);

        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        await Show(nextState);
        await TryOptionalAsync("agent", () => InoModelSetup.ConfigureOpenAgentAsync(Setup, agentKey, settings, effectiveModel, migrate));
        if (await InoModelSetup.EnsureChatAsync(Setup, agentKey)) { await InoModelSetup.ProjectCurrentChatAsync(Setup, agentKey); }
    }

    [On] public async Task OnGrantsChanged(GrantsChanged? changed) { if (changed is not null) { await Show(State with { GrantSummaries = await InoModelSetup.ReadGrantSummariesAsync(Setup, State.GrantSummaries) }); } }
    [On] public Task New(InoNew request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task NewConversation(InoNewConversation request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }
    [On] public Task OnConfirmationRequested(ConfirmationRequested? requested) => requested is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnConfirmationResolved(ConfirmationResolved? resolved) => resolved is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;
    [On] public Task OnFlowsChanged(FlowsChanged? changed) => changed is { FlowId.Length: > 0 } ? RefreshApprovalsAsync(skipWhenBothEmpty: true) : Task.CompletedTask;
    [On] public Task OnChatChanged(ChatChanged? changed) => IsCurrentChat(changed?.Name) ? InoModelSetup.ProjectCurrentChatAsync(Setup, InoModel.AgentKeyOf(State.ConversationId)) : Task.CompletedTask;

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
        await Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)).Post(string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text, ChatAppCard.LinkPostId(request.Request));
    }

    [On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ExtractSendText(request) is not { } text) { return; }
        var settings = await Get<IUserSettings>("settings").Read();
        if (text.StartsWith('/')) { await InoModelSetup.HandleSlashCommandAsync(Setup, text, settings, StartConversationAsync); return; }
        if (await ReadOptionalAsync("compute", false, async () => (await Get<ICompute>("compute").Read()).BudgetExceeded))
        {
            await Show(InoModel.BudgetExceeded(State));
            return;
        }
        var decision = InoModel.DecideSend(State, text, Guid.NewGuid().ToString("N"), NowUtc());
        if (decision.IsRefused || decision.Next.Pending is not { } pending) { return; }
        await Show(decision.Next);
        await InoModelSetup.ExecuteTurnAsync(Setup, settings, pending);
    }

    [On]
    public async Task Retry(InoRetry request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (InoModel.DecideRetryPrompt(State) is { IsAccepted: true, Next: var lastPrompt }) { await Send(new InoSend(Message: lastPrompt)); }
    }

    [On]
    public async Task UseLocalModel(InoUseLocalModel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await InoModelSetup.ApplyLocalModelAsync(Setup, request)) { await Retry(new InoRetry()); }
    }

    [On]
    public async Task SaveProvider(InoSaveProvider request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await InoModelSetup.SaveProviderAsync(Setup, request)) { await Retry(new InoRetry()); }
    }

    [On]
    public async Task OnChatRunChanged(ChatRunChanged? changed)
    {
        if (!IsCurrentChat(changed?.Name)) { return; }
        await InoModelSetup.ProjectCurrentChatAsync(Setup, InoModel.AgentKeyOf(State.ConversationId));
        if (changed!.Status == ChatRunStatus.Completed)
        {
            await TryOptionalAsync("compute-record", () => Get<ICompute>("compute").Record("ino", Math.Max(1_000L, ((State.Messages ?? []).LastOrDefault(m => m.Role == "assistant")?.Text?.Length ?? 0) * 25L)));
            await RefreshApprovalsAsync();
        }
    }

    [On]
    public async Task OnAgentBlocked(AgentBlocked? blocked)
    {
        if (blocked is null || string.IsNullOrEmpty(blocked.Agent) || !IsCurrentAgent(blocked.Agent)) { return; }
        await RefreshApprovalsAsync(blocked.Blocked ? InoModel.Pause(State, "An interrupt holds the agent.") : InoModel.Resume(State));
    }

    [On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryOptionalAsync("agent", async () => { var agent = Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)); if (State.ActiveRunId is not null || (await agent.Read()).ActiveRun is not null) { await agent.Cancel(); } });
    }

    [On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = await Get<IUserSettings>("settings").SetVariable(InoModelSetup.SmartVariable, request.On ? "on" : "off");
        await Show(State with { Smart = InoModelSetup.IsSmart(updated) });
    }

    [On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { var data = Get<IInoData>(DataKey); var result = await data.Connect(request.ConnectionString ?? string.Empty); await Show(InoModel.DecideConnectDb(State, (await data.Read()).ConnectedDatabase, result.Text, result.Card)); }
        catch (Exception error) when (error is not OperationCanceledException) { if (_logger is { } log) { InoLog.LogDatabaseFailed(log, error, "connect-db"); } await Show(State with { Status = $"Could not connect: {error.Message}" }); }
    }

    [On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs); }
        catch (Exception error) when (error is not OperationCanceledException) { if (_logger is { } log) { InoLog.LogDatabaseFailed(log, error, "query-db"); } await Show(State with { Status = $"Could not run the query: {error.Message}" }); return; }
        if (TryGetOptional<IMemory>("memory") is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6)); }
        await Show(InoModel.DecideQueryDbSuccess(State, result, database));
    }

    [On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetOptional<IAppStudio>("main") is { } studio) { target = await ReadOptionalAsync("app-studio", target, async () => (await studio.Build(sentence)).Name); }
        if (TryGetOptional<IMemory>("memory") is { } memory) { await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6)); }
        await Show(InoModel.DecideMake(State, target, sentence, CurrentBrain.Value));
    }

    [On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null || (string.IsNullOrEmpty(changed.DisplayName) && string.IsNullOrEmpty(changed.DefaultModel))) { return; }
        await InoModelSetup.SyncSettingsAsync(Setup, changed);
    }

    private InoSetupHost Setup => _setupHost ??= new InoSetupHost(
        () => State, s => Show(s), () => CurrentBrain,
        () => Get<IModelCatalog>(new NeuronId(CurrentBrain, "ai.models", ModelsKey)),
        () => Get<IUserSettings>("settings"), Get<IAgent>, Get<IChat>, Get<IToneObserver>,
        () => Get<ISpeechRecognizer>("default"), () => Get<IGrantsNeuron>(IGrantsNeuron.DefaultKey), () => Get<IApprovals>(ApprovalsKey),
        () => Get<IInoConversations>(IInoConversations.DefaultKey), () => Get<IProfileContextWriter>("ino/profile-writer"),
        Follow<IAgent, AgentBlocked>,
        async chatKey => { await Follow<IChat, ChatChanged>(chatKey); await Follow<IChat, ChatRunChanged>(chatKey); },
        TryOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync, ReadOptionalAsync,
        LogOptional, _logger);

    private async Task StartConversationAsync()
    {
        var conversationId = InoModel.MintConversationId();
        var agentKey = InoModel.AgentKeyOf(conversationId);
        await Show(InoModel.NewConversation(State, conversationId));
        await InoModelSetup.FollowAgentControlSignalsAsync(Setup, agentKey);
        await InoModelSetup.EnsureChatAsync(Setup, agentKey);
    }

    private async Task RefreshApprovalsAsync(InoAppState? baseState = null, bool skipWhenBothEmpty = false)
    {
        var approvals = await InoModelSetup.ReadApprovalsAsync(Setup, State.Approvals);
        var target = baseState ?? State;
        if (skipWhenBothEmpty && baseState is null && approvals.Length == 0 && (target.Approvals?.Length ?? 0) == 0) { return; }
        await Show(target with { Approvals = approvals });
    }

    private static string? ExtractSendText(InoSend r) => string.IsNullOrWhiteSpace(r.EffectiveMessage) ? null : r.EffectiveMessage.Length > InoModel.MaxMessageChars ? r.EffectiveMessage : r.EffectiveMessage.Trim();
    private bool IsCurrentAgent(string agentId) => string.Equals(agentId, InoModel.AgentKeyOf(State.ConversationId), StringComparison.Ordinal);
    private bool IsCurrentChat(string? chatName) => !string.IsNullOrEmpty(chatName) && string.Equals(chatName, $"{CurrentBrain.Value}/{InoModel.ChatKeyOf(State.ConversationId)}", StringComparison.Ordinal);
    private DateTimeOffset NowUtc() { if (_time is not null) { return _time.GetUtcNow(); } try { return Context.Time.GetUtcNow(); } catch (InvalidOperationException error) { LogOptional(error, "clock"); return TimeProvider.System.GetUtcNow(); } }
    private BrainId CurrentBrain { get { try { return Id.Brain; } catch (InvalidOperationException error) { LogOptional(error, "brain-id"); return default; } } }
    private T? TryGetOptional<T>(string key) where T : class { try { return Get<T>(key); } catch (InvalidOperationException error) { LogOptional(error, typeof(T).Name); return null; } }
    private async Task TryOptionalAsync(string target, Func<Task> action) { try { await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); } }
    private async Task<T> ReadOptionalAsync<T>(string target, T fallback, Func<Task<T>> action) { try { return await action(); } catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); return fallback; } }
    private void LogOptional(Exception error, string target) { if (_logger is { } log) { InoLog.LogOptionalUnavailable(log, error, target); } }
}
