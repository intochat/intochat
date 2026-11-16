/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, save-provider, connect-db, query-db, new, new-conversation, make, link

The brain's personal assistant. Every message starts a background run on one agent (IAgent) per conversation while Ino projects
the conversation through ChatNeuron (IChat) into pure InoModel state and renders a Conversation node via InoView.
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
using IntoChat.OS.Modules.AI.Models.Anthropic;
using IntoChat.OS.Modules.AI.Models.Google;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.AI.Models.OpenRouter;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Compute;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

[Signal("ino.open")] public sealed record InoOpen(string? Mode = null) : Signal;
[Signal("ino.retry")] public sealed record InoRetry : Signal;
[Signal("ino.use-local")] public sealed record InoUseLocalModel(string? Model = null, string? Value = null) : Signal;
[Signal("ino.save-provider")] public sealed record InoSaveProvider(string? Value = null, string? Provider = null) : Signal;
[Signal("ino.link")] public sealed record InoPasteLink(Guid Request = default, string? App = null, string? Input = null, string? Text = null) : Signal;
[Signal("ino.new")] public sealed record InoNew : Signal;
[Signal("ino.new-conversation")] public sealed record InoNewConversation : Signal;
[Signal("ino.make")] public sealed record InoMake(string? Sentence = null, string[]? Focus = null) : Signal;
[Signal("ino.stop")] public sealed record InoStop : Signal;
[Signal("ino.smart")] public sealed record InoSmart(bool On = true) : Signal;
[Signal("ino.connect-db")] public sealed record InoConnectDb(string? ConnectionString = null, string? Alias = null) : Signal;
[Signal("ino.query-db")] public sealed record InoQueryDb(string? Sql = null) : Signal;

[Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null, string? Value = null) : Signal
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : !string.IsNullOrWhiteSpace(Text) ? Text : Value;
}

[Neuron("app.ino")]
public sealed class InoApp : Screen<InoAppState>, IWithFilesVfs
{
    private const string SmartVariable = "ino.smart";
    private const string ApprovalsKey = "approvals/index";
    private const string DataKey = "ino/data";
    private const string ModelsKey = "main";

    private readonly ILogger<InoApp>? _logger;
    private bool _canDictate;

    public InoApp() : this(null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public InoApp(ILogger<InoApp>? logger = null) => _logger = logger;

    public bool CanDictate => _canDictate;

    public IFilesVfsService? FilesVfs { get; set; }
    public const string AppName = "ino";

    public static AppBundle Bundle { get; } = new(
        "ino",
        "3.6.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, query databases, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Send a message to Ino's agent."),
                new("retry", "Send the last message again."),
                new("stop", "Stop the running turn."),
                new("smart", "Turn smart mode on or off."),
                new("use-local", "Bind a locally detected Ollama model."),
                new("save-provider", "Save a cloud provider credential to the brain's Secure store."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
                new("link", "Post a pasted workspace link: its app, opaque input and text."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 1),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(InoApp).AssemblyQualifiedName,
    };

    public new InoAppState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => InoView.Side(State);
    protected override View Render(InoAppState state) => InoView.Screen(state);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        await TryOptionalAsync("settings", () => Follow<IUserSettings, UserSettingsChanged>("settings"));
        await TryOptionalAsync("confirmations-synapses", () => Context.Synapses.EnsureBrainWide([typeof(ConfirmationRequested), typeof(ConfirmationResolved), typeof(FlowsChanged)]));
        await InitializeActivationViewAsync();
        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        await RegisterConversationAsync(agentKey);
        if (await EnsureChatAsync(agentKey)) { await ProjectCurrentChatAsync(agentKey); }
    }

    [On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var local = await DetectLocalModelsAsync();
        _canDictate = await ReadCanDictateAsync();
        var resolvedChoice = await ResolveEffectiveModelAsync(settings, local);
        var (nextState, migrate) = InoModel.DecideOpen(
            State, request.Mode, settings.DisplayName, resolvedChoice.DisplayModel,
            IsSmart(settings), await ReadApprovalsAsync(), await ReadGrantSummariesAsync(),
            local?.PreferredModel, local?.InstalledModels);

        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        await RegisterConversationAsync(agentKey);
        await Show(nextState);

        await TryOptionalAsync("agent", async () =>
        {
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(agentKey);
            if (migrate)
            {
                await agent.Post(InoModel.MigrationNote, InoModel.MigrationId);
                await Show(State with { AgentKey = agentKey });
            }

            await EnsureConfiguredAsync(agent, settings, resolvedChoice.EffectiveModel);
            await FollowAgentControlSignalsAsync(agentKey);
            var read = await agent.Read();
            if (read.Blocked) { await Show(InoModel.Pause(State, State.BlockedReason)); }
            else if (State.Blocked) { await Show(InoModel.Resume(State)); }
        });

        if (await EnsureChatAsync(agentKey)) { await ProjectCurrentChatAsync(agentKey); }
    }

    [On]
    public async Task OnGrantsChanged(GrantsChanged? changed)
    {
        if (changed is null) { return; }
        await Show(State with { GrantSummaries = await ReadGrantSummariesAsync() });
    }

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
        {
            await records.Write(kept with { Links = [.. kept.Links.TakeLast(ChatAppCard.MaxLinksKept - 1), new InoLink(id, request.App!, input)] });
        }

        await Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)).Post(
            string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text,
            ChatAppCard.LinkPostId(request.Request));
    }

    [On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ExtractSendText(request) is not { } text) { return; }

        var settings = await Get<IUserSettings>("settings").Read();
        if (text.StartsWith('/'))
        {
            await HandleSlashCommandAsync(text, settings);
            return;
        }

        if (await ReadOptionalAsync("compute", false, async () => (await Get<ICompute>("compute").Read()).BudgetExceeded))
        {
            await Show(InoModel.BudgetExceeded(State));
            return;
        }

        var decision = InoModel.DecideSend(State, text, Guid.NewGuid().ToString("N"), NowUtc());
        if (decision.IsRefused) { return; }

        await Show(decision.Next);
        if (decision.Next.Pending is not { } pending) { return; }

        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        var local = await DetectLocalModelsAsync();
        if (local is not null && (State.DetectedLocalModel != local.PreferredModel || State.DetectedLocalModels is null))
        {
            await Show(State with { DetectedLocalModel = local.PreferredModel, DetectedLocalModels = local.InstalledModels });
        }

        await EnsureChatAsync(agentKey);
        try
        {
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(agentKey);
            var resolvedChoice = await ResolveEffectiveModelAsync(settings, local);
            await EnsureConfiguredAsync(agent, settings, resolvedChoice.EffectiveModel);
            await FollowAgentControlSignalsAsync(agentKey);
            var runId = await agent.Start(AgentTurn.Text(pending.Text, model: resolvedChoice.EffectiveModel));
            await Show(InoModel.Started(State, runId, resolvedChoice.DisplayModel));
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            await Show(InoModel.StartFailed(State, new InoError("paused", error.Message[InoModel.BlockedPrefix.Length..])));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_logger is { } log) { InoLog.LogTurnStartFailed(log, error, agentKey); }
            var code = AgentFailures.CodeOf(AgentFailures.Classify(error));
            if (code == "provider-unavailable" && await IsMissingLocalOllamaAsync(local))
            {
                code = "no-provider";
            }
            await Show(InoModel.StartFailed(State, new InoError(code)));
        }
    }

    [On]
    public async Task Retry(InoRetry request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (InoModel.DecideRetryPrompt(State) is { IsAccepted: true, Next: var lastPrompt })
        {
            await Send(new InoSend(Message: lastPrompt));
        }
    }

    [On]
    public async Task UseLocalModel(InoUseLocalModel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = await DetectLocalModelsAsync();
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim()
            : !string.IsNullOrWhiteSpace(request.Value) ? request.Value.Trim()
            : local?.PreferredModel ?? State.DetectedLocalModel ?? ModelCatalog.DefaultChatModelId;

        await PersistDefaultModelAsync(model);
        var hadProviderFailure = State.Error?.Code is "no-provider" or "provider-rejected-key";
        await Show(InoModel.ModelSelected(State, model, $"Switched model to {model}", local));
        if (hadProviderFailure) { await Retry(new InoRetry()); }
    }

    [On]
    public async Task SaveProvider(InoSaveProvider request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Value?.Trim() is not { Length: > 0 } credential) { return; }

        var provider = !string.IsNullOrWhiteSpace(request.Provider)
            ? request.Provider.Trim().ToLowerInvariant()
            : InferProviderFromCredential(credential);

        try
        {
            await Catalog.SaveProviderCredential(provider, credential);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_logger is { } log) { InoLog.LogProviderSaveFailed(log, error, provider); }
            await Show(State with { Error = new InoError("unknown"), Status = InoModel.Failed });
            return;
        }

        var defaultModel = DefaultModelForProvider(provider);
        await PersistDefaultModelAsync(defaultModel);
        var hadProviderFailure = State.Error?.Code is "no-provider" or "provider-rejected-key";
        await Show(InoModel.ModelSelected(State, defaultModel, $"Saved {provider} credential · Ready on {defaultModel}"));
        if (hadProviderFailure) { await Retry(new InoRetry()); }
    }

    [On]
    public Task OnChatChanged(ChatChanged? changed)
        => IsCurrentChat(changed?.Name) ? ProjectCurrentChatAsync(InoModel.AgentKeyOf(State.ConversationId)) : Task.CompletedTask;

    [On]
    public async Task OnChatRunChanged(ChatRunChanged? changed)
    {
        if (!IsCurrentChat(changed?.Name)) { return; }
        await ProjectCurrentChatAsync(InoModel.AgentKeyOf(State.ConversationId));
        if (changed!.Status == ChatRunStatus.Completed)
        {
            await RecordComputeAsync((State.Messages ?? []).LastOrDefault(m => m.Role == "assistant")?.Text);
            await RefreshApprovalsAsync();
        }
    }

    [On]
    public async Task OnAgentBlocked(AgentBlocked? blocked)
    {
        if (blocked is null || string.IsNullOrEmpty(blocked.Agent) || !IsCurrentAgent(blocked.Agent)) { return; }
        var next = blocked.Blocked ? InoModel.Pause(State, "An interrupt holds the agent.") : InoModel.Resume(State);
        await Show(next with { Approvals = await ReadApprovalsAsync() });
    }

    [On]
    public Task OnConfirmationRequested(ConfirmationRequested? requested)
        => requested is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;

    [On]
    public Task OnConfirmationResolved(ConfirmationResolved? resolved)
        => resolved is { PlanId.Length: > 0 } ? RefreshApprovalsAsync() : Task.CompletedTask;

    [On]
    public async Task OnFlowsChanged(FlowsChanged? changed)
    {
        if (changed is not { FlowId.Length: > 0 }) { return; }
        var approvals = await ReadApprovalsAsync();
        if (approvals.Length > 0 || (State.Approvals?.Length ?? 0) > 0) { await Show(State with { Approvals = approvals }); }
    }

    [On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryOptionalAsync("agent", async () =>
        {
            var agent = Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId));
            if (State.ActiveRunId is not null || (await agent.Read()).ActiveRun is not null) { await agent.Cancel(); }
        });
    }

    [On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = await Get<IUserSettings>("settings").SetVariable(SmartVariable, request.On ? "on" : "off");
        await Show(State with { Smart = IsSmart(updated) });
    }

    [On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var data = Get<IInoData>(DataKey);
            var result = await data.Connect(request.ConnectionString ?? string.Empty);
            await Show(InoModel.DecideConnectDb(State, (await data.Read()).ConnectedDatabase, result.Text, result.Card));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_logger is { } log) { InoLog.LogDatabaseFailed(log, error, "connect-db"); }
            await Show(State with { Status = $"Could not connect: {error.Message}" });
        }
    }

    [On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_logger is { } log) { InoLog.LogDatabaseFailed(log, error, "query-db"); }
            await Show(State with { Status = $"Could not run the query: {error.Message}" });
            return;
        }

        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6));
        }
        await Show(InoModel.DecideQueryDbSuccess(State, result, database));
    }

    [On]
    public Task New(InoNew request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }

    [On]
    public Task NewConversation(InoNewConversation request) { ArgumentNullException.ThrowIfNull(request); return StartConversationAsync(); }

    [On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetOptional<IAppStudio>("main") is { } studio)
        {
            target = await ReadOptionalAsync("app-studio", target, async () => (await studio.Build(sentence)).Name);
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await TryOptionalAsync("memory", () => memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6));
        }
        await Show(InoModel.DecideMake(State, target, sentence, CurrentBrainId));
    }

    [On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null || (string.IsNullOrEmpty(changed.DisplayName) && string.IsNullOrEmpty(changed.DefaultModel))) { return; }
        var settings = await Get<IUserSettings>("settings").Read();
        var local = await DetectLocalModelsAsync();
        var resolved = await ResolveEffectiveModelAsync(settings, local);
        await EnsureProfileContextAsync();
        await TryOptionalAsync("agent", () => EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), settings, resolved.EffectiveModel));
        await Show(InoModel.SyncedSettings(State, IsSmart(settings), resolved.DisplayModel, changed.DisplayName, resolved.DisplayModel));
    }

    private async Task StartConversationAsync()
    {
        var conversationId = InoModel.MintConversationId();
        var agentKey = InoModel.AgentKeyOf(conversationId);
        await Show(InoModel.NewConversation(State, conversationId));
        await FollowAgentControlSignalsAsync(agentKey);
        await EnsureChatAsync(agentKey);
    }

    private async Task PersistDefaultModelAsync(string model)
    {
        var settingsNeuron = Get<IUserSettings>("settings");
        var current = await settingsNeuron.Read();
        var updated = await settingsNeuron.SetProfile(current.DisplayName, current.Theme, model);
        await EnsureProfileContextAsync();
        await TryOptionalAsync("agent", () => EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), updated, model));
    }

    private async Task<bool> EnsureChatAsync(string agentKey)
    {
        var chatKey = InoModel.ChatKeyOf(State.ConversationId);
        var fullAgentKey = $"{CurrentBrainId}/{agentKey}";
        try
        {
            var chat = Get<IChat>(chatKey);
            var current = await chat.Read();
            if (!string.Equals(current.Selection?.Agent, fullAgentKey, StringComparison.Ordinal))
            {
                await chat.SelectAgent(fullAgentKey, current.Selection?.Revision ?? 0, Guid.NewGuid());
            }
            await Follow<IChat, ChatChanged>(chatKey);
            await Follow<IChat, ChatRunChanged>(chatKey);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, "chat");
            return false;
        }
    }

    private async Task ProjectCurrentChatAsync(string agentKey)
    {
        var chatState = await ReadOptionalAsync<ChatState?>("chat-read", null, () => Get<IChat>(InoModel.ChatKeyOf(State.ConversationId)).Read()!);
        if (chatState is null) { return; }

        var next = InoModel.Projected(State, chatState, $"{CurrentBrainId}/{agentKey}");
        if (next.Error?.Code == "provider-unavailable" && await IsMissingLocalOllamaAsync(local: null))
        {
            next = next with { Error = new InoError("no-provider") };
        }
        if (!ReferenceEquals(next, State))
        {
            await Show(next);
        }
    }

    private Task RegisterConversationAsync(string agentKey)
        => TryOptionalAsync("conversations-registry", () => Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey));

    private async Task FollowAgentControlSignalsAsync(string agentKey)
    {
        await RegisterConversationAsync(agentKey);
        await TryOptionalAsync("agent-synapses", () => Follow<IAgent, AgentBlocked>(agentKey));
    }

    private Task EnsureProfileContextAsync()
        => TryOptionalAsync("profile-context", () => Get<IProfileContextWriter>("ino/profile-writer").Ensure());

    private Task RecordComputeAsync(string? reply)
        => TryOptionalAsync("compute-record", () => Get<ICompute>("compute").Record("ino", Math.Max(1_000L, (reply?.Length ?? 0) * 25L)));

    private async Task RefreshApprovalsAsync() => await Show(State with { Approvals = await ReadApprovalsAsync() });

    private Task<Approval[]> ReadApprovalsAsync()
        => ReadOptionalAsync("approvals", State.Approvals ?? [], async () => [.. await Get<IApprovals>(ApprovalsKey).Pending()]);

    private Task<string[]> ReadGrantSummariesAsync()
        => ReadOptionalAsync("grants", State.GrantSummaries ?? [], async () =>
        {
            var grants = await Get<IGrantsNeuron>(IGrantsNeuron.DefaultKey).List();
#pragma warning disable CS0618
            var assistant = DigitalBrain.Capabilities.CapabilityCallers.AssistantApp;
#pragma warning restore CS0618
            return InoModel.SummarizeGrants(grants, assistant);
        });

    private IModelCatalog Catalog
        => Get<IModelCatalog>(new NeuronId(new BrainId(CurrentBrainId), "ai.models", ModelsKey));

    private async Task InitializeActivationViewAsync()
    {
        _canDictate = await ReadCanDictateAsync();
        if (!string.IsNullOrWhiteSpace(State.SelectedModel)) { return; }

        var settings = await ReadOptionalAsync<UserSettingsState?>("settings-read", null, async () => await Get<IUserSettings>("settings").Read());
        if (settings is null) { return; }

        var local = await DetectLocalModelsAsync();
        var resolved = await ResolveEffectiveModelAsync(settings, local);
        var approvals = await ReadApprovalsAsync();
        var grantSummaries = await ReadGrantSummariesAsync();
        await Show(State with
        {
            SelectedModel = resolved.DisplayModel,
            Smart = IsSmart(settings),
            Approvals = approvals,
            GrantSummaries = grantSummaries,
            DetectedLocalModel = local?.PreferredModel ?? State.DetectedLocalModel,
            DetectedLocalModels = local?.InstalledModels ?? State.DetectedLocalModels,
        });
    }

    private Task<bool> ReadCanDictateAsync()
        => ReadOptionalAsync("speech-recognizer", false, async () =>
            (await Get<ISpeechRecognizer>("default").Describe()).Available);

    private Task<LocalModelSummary?> DetectLocalModelsAsync()
        => ReadOptionalAsync<LocalModelSummary?>("local-models", null, async () =>
        {
            var summary = await Catalog.DetectLocal();
            return summary.Available ? summary : null;
        });

    private Task<ModelChoices?> ReadModelChoicesAsync()
        => ReadOptionalAsync<ModelChoices?>("model-catalog", null, async () => await Catalog.List(null));

    private async Task<(string? EffectiveModel, string DisplayModel)> ResolveEffectiveModelAsync(UserSettingsState settings, LocalModelSummary? local)
    {
        var personModel = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim();
        if (personModel is not null)
        {
            return (personModel, personModel);
        }

        var choices = await ReadModelChoicesAsync();
        if (choices is not null)
        {
            var auto = choices.Automatic;
            if (!string.IsNullOrWhiteSpace(auto.Model) && !string.IsNullOrWhiteSpace(auto.Provider))
            {
                return (auto.Model, auto.Model);
            }

            // When catalog reports Host (no provider/endpoint configured yet), preserve null for AgentTurn fallback.
            var fallbackDisplay = local?.PreferredModel is { Length: > 0 } p ? p : ModelCatalog.DefaultChatModelId;
            return (null, fallbackDisplay);
        }

        var fallback = local?.PreferredModel is { Length: > 0 } detected ? detected : ModelCatalog.DefaultChatModelId;
        return (fallback, fallback);
    }

    private async Task<bool> IsMissingLocalOllamaAsync(LocalModelSummary? local)
    {
        var choices = await ReadModelChoicesAsync();
        if (choices is null)
        {
            return false;
        }

        if (!string.Equals(choices.Automatic.Provider, AiProvider.Ollama.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (choices.Models.Any(m => m.Available && !string.Equals(m.Provider, AiProvider.Ollama.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var detected = local ?? await DetectLocalModelsAsync();
        return detected is not { Available: true };
    }

    private async Task EnsureConfiguredAsync(IAgent agent, UserSettingsState settings, string? effectiveModel)
    {
        var effectiveSettings = string.IsNullOrWhiteSpace(effectiveModel)
            ? settings
            : settings with { DefaultModel = effectiveModel.Trim() };

        var current = await agent.Read();
        var definition = InoAgentDefinition.For(effectiveSettings);
        if (current.Revision == 0)
        {
            await TryOptionalAsync("agent-configure-conflict", () => agent.Configure(definition, 0));
            await TryOptionalAsync("tone-observer", () => Get<IToneObserver>(InoModel.ToneKeyOf(State.ConversationId)).Follow(InoModel.AgentKeyOf(State.ConversationId)));
            return;
        }

        if (current.ActiveRun is null
            && (!string.Equals(current.Definition.Model?.Model, definition.Model?.Model, StringComparison.Ordinal)
                || !string.Equals(current.Definition.Instructions, definition.Instructions, StringComparison.Ordinal)))
        {
            await TryOptionalAsync("agent-reconfigure-conflict", () => agent.Configure(definition, current.Revision));
        }
    }

    private async Task HandleSlashCommandAsync(string rawCommand, UserSettingsState settings)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "new" or "clear":
                await StartConversationAsync();
                return;
            case "pin":
                await Show(InoModel.DecideSlashPin(State));
                return;
            case "rename":
                await Show(InoModel.DecideSlashRename(State, arg));
                return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? ModelCatalog.DefaultChatModelId : arg.Trim();
                await PersistDefaultModelAsync(nextModel);
                await Show(InoModel.ModelSelected(State, nextModel, $"Switched model to {nextModel}"));
                return;
            case "scenario" or "session":
                await Show(InoModel.DecideScenario(arg, settings));
                return;
            default:
                await Show(InoModel.DecideSlashHelp(State, rawCommand, cmd));
                return;
        }
    }

    private static string? ExtractSendText(InoSend r)
        => string.IsNullOrWhiteSpace(r.EffectiveMessage) ? null
            : r.EffectiveMessage.Length > InoModel.MaxMessageChars ? r.EffectiveMessage
            : r.EffectiveMessage.Trim();

    private static bool IsSmart(UserSettingsState s) => s.Variables.Any(v => v.Key == SmartVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));
    private bool IsCurrentAgent(string agentId) => string.Equals(agentId, InoModel.AgentKeyOf(State.ConversationId), StringComparison.Ordinal);
    private bool IsCurrentChat(string? chatName) => !string.IsNullOrEmpty(chatName) && string.Equals(chatName, $"{CurrentBrainId}/{InoModel.ChatKeyOf(State.ConversationId)}", StringComparison.Ordinal);

    private DateTimeOffset NowUtc()
    {
        try { return Context.Time.GetUtcNow(); }
        catch (InvalidOperationException error)
        {
            LogOptional(error, "clock");
            return DateTimeOffset.UtcNow;
        }
    }

    private static string InferProviderFromCredential(string credential)
        => credential.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) ? "anthropic"
            : credential.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase) ? "openrouter"
            : credential.StartsWith("AIza", StringComparison.Ordinal) ? "google" : "openai";

    private static string DefaultModelForProvider(string provider) => provider switch
    {
        "anthropic" => CatalogId(typeof(ISonnet46)),
        "google" => CatalogId(typeof(IGemini36Pro)),
        "openrouter" => CatalogId(typeof(IDeepSeekV41Flash)),
        _ => CatalogId(typeof(IGpt54)),
    };

    private static string CatalogId(Type marker)
        => LLMModel.FindByMarker(marker)?.Id ?? ModelCatalog.DefaultChatModelId;

    private string CurrentBrainId
    {
        get
        {
            try { return Id.Brain.Value is { Length: > 0 } b ? b : "local"; }
            catch (InvalidOperationException error)
            {
                LogOptional(error, "brain-id");
                return "local";
            }
        }
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            LogOptional(error, typeof(T).Name);
            return null;
        }
    }

    private async Task TryOptionalAsync(string target, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is not OperationCanceledException) { LogOptional(error, target); }
    }

    private async Task<T> ReadOptionalAsync<T>(string target, T fallback, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogOptional(error, target);
            return fallback;
        }
    }

    private void LogOptional(Exception error, string target)
    {
        if (_logger is { } log) { InoLog.LogOptionalUnavailable(log, error, target); }
    }
}

internal static partial class InoLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Ino optional dependency '{Target}' was not available.")]
    public static partial void LogOptionalUnavailable(ILogger logger, Exception exception, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ino failed to start turn on agent '{AgentKey}'.")]
    public static partial void LogTurnStartFailed(ILogger logger, Exception exception, string agentKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ino database operation '{Operation}' failed.")]
    public static partial void LogDatabaseFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ino failed to save credential for provider '{Provider}'.")]
    public static partial void LogProviderSaveFailed(ILogger logger, Exception exception, string provider);
}
