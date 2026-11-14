/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, retry, stop, smart, use-local, save-provider, connect-db, query-db, new, new-conversation, voice, make, link

The brain's personal assistant. Every message starts a background run on one agent (IAgent) per conversation while Ino projects
the conversation through ChatNeuron (IChat) into pure InoModel state and renders a Conversation node via InoView.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

[DigitalBrain.Signal("ino.open")]
public sealed record InoOpen(string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null, string? Value = null) : DigitalBrain.Signal
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : !string.IsNullOrWhiteSpace(Text) ? Text : Value;
}

[DigitalBrain.Signal("ino.retry")]
public sealed record InoRetry : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.use-local")]
public sealed record InoUseLocalModel(string? Model = null, string? Value = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.save-provider")]
public sealed record InoSaveProvider(string? Value = null, string? Provider = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.link")]
public sealed record InoPasteLink(Guid Request = default, string? App = null, string? Input = null, string? Text = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.new")]
public sealed record InoNew : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.new-conversation")]
public sealed record InoNewConversation : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.voice")]
public sealed record InoVoice : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.make")]
public sealed record InoMake(string? Sentence = null, string[]? Focus = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.stop")]
public sealed record InoStop : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.smart")]
public sealed record InoSmart(bool On = true) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.connect-db")]
public sealed record InoConnectDb(string? ConnectionString = null, string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.query-db")]
public sealed record InoQueryDb(string? Sql = null) : DigitalBrain.Signal;

[Neuron("app.ino")]
public sealed class InoApp : Screen<InoAppState>, IWithFilesVfs
{
    private const string SmartVariable = "ino.smart";
    private const string ModelChosenVariable = "ino.model.chosen";
    private const string StockDefaultModel = "qwen3.5:4b";
    private const string ApprovalsKey = "approvals/index";
    private const string DataKey = "ino/data";
    private const string ModelsKey = "main";

    private static readonly ILoggerFactory FallbackLoggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Warning));
    private static readonly ILogger FallbackLogger = FallbackLoggerFactory.CreateLogger<InoApp>();

    private bool _chatBound;

    public ILogger<InoApp>? Logger { get; set; }
    public IFilesVfsService? FilesVfs { get; set; }

    private ILogger Log => Logger ?? FallbackLogger;

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
                new("voice", "Begin voice input."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
                new("link", "Post a pasted workspace link: its app, opaque input and text."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 1,
            Toolbar:
            [
                new("send", Kind: "text", Label: "Message your brain"),
                new("new-conversation", Icon: "chat", Label: "New conversation"),
                new("smart", Icon: "sparkles", Label: "Smart mode"),
                new("voice", Icon: "mic", Label: "Voice"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(InoApp).AssemblyQualifiedName,
    };

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        try { await Follow<IUserSettings, UserSettingsChanged>("settings"); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "settings"); }

        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        if (await EnsureChatAsync(agentKey))
        {
            await ProjectCurrentChatAsync(agentKey);
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var smart = IsSmart(settings);
        var approvals = await ReadApprovalsAsync();
        var grants = await ReadGrantSummariesAsync();
        var local = await DetectLocalModelsAsync();

        var (nextState, migrate) = InoModel.DecideOpen(
            State,
            request.Mode,
            settings.DisplayName,
            settings.DefaultModel,
            smart,
            approvals,
            grants,
            local?.PreferredModel,
            local?.InstalledModels);
        var agentKey = InoModel.AgentKeyOf(State.ConversationId);
        await Show(nextState);

        try
        {
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(agentKey);
            if (migrate)
            {
                await agent.Post(InoModel.MigrationNote, InoModel.MigrationId);
                await Show(State with { AgentKey = agentKey });
            }

            await EnsureConfiguredAsync(agent, settings, local);
            await FollowAgentControlSignalsAsync(agentKey);
            var read = await agent.Read();
            if (read.Blocked)
            {
                await Show(InoModel.Pause(State, State.BlockedReason));
            }
            else if (State.Blocked)
            {
                await Show(InoModel.Resume(State));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "agent");
        }

        if (await EnsureChatAsync(agentKey))
        {
            await ProjectCurrentChatAsync(agentKey);
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnGrantsChanged(GrantsChanged? changed)
    {
        _ = changed;
        var grants = await ReadGrantSummariesAsync();
        await Show(State with { GrantSummaries = grants });
    }

    [DigitalBrain.Neurons.On]
    public async Task Link(InoPasteLink request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Request == Guid.Empty || !ChatAppCard.IsAppName(request.App)) { return; }
        var input = request.Input ?? "";
        if (input.Length > ChatAppCard.MaxInput) { throw new ArgumentException("The link input is too long."); }
        var id = request.Request.ToString("N");
        var records = Get<IInoRecords>(IInoRecords.Key);
        var kept = await records.Read();
        if (!kept.Links.Any(link => link.Request == id))
        {
            await records.Write(kept with { Links = [.. kept.Links.TakeLast(ChatAppCard.MaxLinksKept - 1), new InoLink(id, request.App!, input)] });
        }
        await Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)).Post(string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text, ChatAppCard.LinkPostId(request.Request));
    }

    [DigitalBrain.Neurons.On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string text;
        if (request.Message is null && request.Text is null && request.Value is null)
        {
            text = "hello";
        }
        else
        {
            var candidate = request.Message ?? request.Text ?? request.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }
            text = candidate.Length > InoModel.MaxMessageChars ? candidate : candidate.Trim();
        }

        var settings = await Get<IUserSettings>("settings").Read();
        if (text.StartsWith('/'))
        {
            await HandleSlashCommandAsync(text, settings);
            return;
        }

        if (await IsComputeBudgetExceededAsync())
        {
            await Show(InoModel.BudgetExceeded(State));
            return;
        }

        var next = InoModel.Sent(State, text, Guid.NewGuid().ToString("N"), NowUtc());
        if (ReferenceEquals(next, State))
        {
            return;
        }

        await Show(next);
        if (next.Pending is null)
        {
            return;
        }

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
            await EnsureConfiguredAsync(agent, settings, local);
            await FollowAgentControlSignalsAsync(agentKey);
            var effectiveModel = ResolveEffectiveModel(settings, local);
            var runId = await agent.Start(AgentTurn.Text(next.Pending.Text, model: effectiveModel));
            await Show(InoModel.Started(State, runId));
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            var reason = error.Message[InoModel.BlockedPrefix.Length..];
            await Show(InoModel.StartFailed(State, new InoError("paused", reason)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogTurnStartFailed(Log, error, agentKey);
            var code = InoModel.ClassifyErrorText(error.Message);
            await Show(InoModel.StartFailed(State, new InoError(code)));
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task Retry(InoRetry request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State.ActiveRunId is not null || State.Pending is not null)
        {
            return;
        }

        var lastUserText = (State.Messages ?? []).LastOrDefault(m => m.Role == "user")?.Text;
        if (string.IsNullOrWhiteSpace(lastUserText))
        {
            return;
        }

        await Send(new InoSend(Message: lastUserText));
    }

    [DigitalBrain.Neurons.On]
    public async Task UseLocalModel(InoUseLocalModel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = await DetectLocalModelsAsync();
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim()
            : !string.IsNullOrWhiteSpace(request.Value) ? request.Value.Trim()
            : local?.PreferredModel
            ?? State.DetectedLocalModel
            ?? StockDefaultModel;

        var settingsNeuron = Get<IUserSettings>("settings");
        var current = await settingsNeuron.Read();
        await settingsNeuron.SetVariable(ModelChosenVariable, "on");
        var updated = await settingsNeuron.SetProfile(current.DisplayName, current.Theme, model);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), updated, local); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "agent"); }

        var hadProviderFailure = State.Error?.Code is "no-provider" or "provider-rejected-key";
        await Show(State with
        {
            Error = null,
            Status = $"Switched model to {model}",
            DetectedLocalModel = local?.PreferredModel ?? model,
            DetectedLocalModels = local?.InstalledModels ?? State.DetectedLocalModels,
        });

        if (hadProviderFailure)
        {
            await Retry(new InoRetry());
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task SaveProvider(InoSaveProvider request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credential = request.Value?.Trim();
        if (string.IsNullOrWhiteSpace(credential))
        {
            return;
        }

        var provider = !string.IsNullOrWhiteSpace(request.Provider)
            ? request.Provider.Trim().ToLowerInvariant()
            : InferProviderFromCredential(credential);

        try
        {
            await Get<IModelCatalog>(new NeuronId(new BrainId(CurrentBrainId), "ai.models", ModelsKey))
                .SaveProviderCredential(provider, credential);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogProviderSaveFailed(Log, error, provider);
            await Show(State with { Error = new InoError("unknown"), Status = InoModel.Failed });
            return;
        }

        var defaultModel = DefaultModelForProvider(provider);
        var settingsNeuron = Get<IUserSettings>("settings");
        var current = await settingsNeuron.Read();
        await settingsNeuron.SetVariable(ModelChosenVariable, "on");
        var updated = await settingsNeuron.SetProfile(current.DisplayName, current.Theme, defaultModel);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), updated, null); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "agent"); }

        var hadProviderFailure = State.Error?.Code is "no-provider" or "provider-rejected-key";
        await Show(State with
        {
            Error = null,
            Status = $"Saved {provider} credential · Ready on {defaultModel}",
        });

        if (hadProviderFailure)
        {
            await Retry(new InoRetry());
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnChatChanged(ChatChanged? changed)
    {
        if (changed is null || string.IsNullOrEmpty(changed.Name))
        {
            return;
        }

        var expectedChatName = $"{CurrentBrainId}/{InoModel.ChatKeyOf(State.ConversationId)}";
        if (!string.Equals(changed.Name, expectedChatName, StringComparison.Ordinal))
        {
            return;
        }

        await ProjectCurrentChatAsync(InoModel.AgentKeyOf(State.ConversationId));
    }

    [DigitalBrain.Neurons.On]
    public async Task OnChatRunChanged(ChatRunChanged? changed)
    {
        if (changed is null || string.IsNullOrEmpty(changed.Name))
        {
            return;
        }

        var expectedChatName = $"{CurrentBrainId}/{InoModel.ChatKeyOf(State.ConversationId)}";
        if (!string.Equals(changed.Name, expectedChatName, StringComparison.Ordinal))
        {
            return;
        }

        await ProjectCurrentChatAsync(InoModel.AgentKeyOf(State.ConversationId));

        if (changed.Status == ChatRunStatus.Completed)
        {
            var lastReply = (State.Messages ?? []).LastOrDefault(m => m.Role == "assistant")?.Text;
            await RecordComputeAsync(lastReply);
            var approvals = await ReadApprovalsAsync();
            await Show(State with { Approvals = approvals });
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnAgentBlocked(AgentBlocked? blocked)
    {
        if (blocked is null || string.IsNullOrEmpty(blocked.Agent))
        {
            return;
        }

        if (!IsCurrent(blocked.Agent))
        {
            return;
        }

        var next = blocked.Blocked
            ? InoModel.Pause(State, "An interrupt holds the agent.")
            : InoModel.Resume(State);
        var approvals = await ReadApprovalsAsync();
        await Show(next with { Approvals = approvals });
    }

    // Fallback handlers when running in isolated unit tests without ShellModule/ChatNeuron.
    [DigitalBrain.Neurons.On]
    public async Task OnTurnCompleted(TurnCompleted? completed)
    {
        if (_chatBound || completed is null || string.IsNullOrEmpty(completed.AgentId) || !IsCurrent(completed.AgentId))
        {
            return;
        }

        await RecordComputeAsync(completed.Reply);
        var approvals = await ReadApprovalsAsync();
        var userText = State.Pending?.Text;
        var existing = State.Messages ?? [];
        var appended = new List<InoMessage>(existing);
        if (!string.IsNullOrEmpty(userText))
        {
            appended.Add(new InoMessage($"{completed.RunId}/user", "user", userText));
        }
        appended.Add(new InoMessage($"{completed.RunId}/assistant", "assistant", completed.Reply ?? string.Empty));
        await Show(State with
        {
            Pending = null,
            ActiveRunId = null,
            Status = InoModel.Ready,
            Error = null,
            Messages = [.. appended],
            Approvals = approvals,
        });
    }

    [DigitalBrain.Neurons.On]
    public async Task OnTurnFailed(TurnFailed? failed)
    {
        if (_chatBound || failed is null || string.IsNullOrEmpty(failed.AgentId) || !IsCurrent(failed.AgentId))
        {
            return;
        }

        var errorMsg = failed.Error ?? string.Empty;
        if (errorMsg.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            await Show(InoModel.Pause(State, errorMsg));
            return;
        }

        if (failed.Cancelled)
        {
            await Show(State with { Pending = null, ActiveRunId = null, Status = InoModel.Ready, Error = null });
            return;
        }

        var code = AgentFailures.CodeOf(failed.Failure) ?? InoModel.ClassifyErrorText(errorMsg);
        var userText = State.Pending?.Text;
        var existing = State.Messages ?? [];
        var appended = new List<InoMessage>(existing);
        if (!string.IsNullOrEmpty(userText))
        {
            appended.Add(new InoMessage($"{failed.RunId}/user", "user", userText));
        }
        appended.Add(new InoMessage($"{failed.RunId}/assistant", "assistant", string.Empty, "failed") { Failure = code });
        await Show(State with
        {
            Pending = null,
            ActiveRunId = null,
            Status = InoModel.Failed,
            Error = new InoError(code),
            Messages = [.. appended],
        });
    }

    [DigitalBrain.Neurons.On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var agent = Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId));
            if (State.ActiveRunId is null)
            {
                var read = await agent.Read();
                if (read.ActiveRun is null)
                {
                    return;
                }
            }
            await agent.Cancel();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "agent");
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = await Get<IUserSettings>("settings").SetVariable(SmartVariable, request.On ? "on" : "off");
        await Show(State with { Smart = IsSmart(updated) });
    }

    [DigitalBrain.Neurons.On]
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
            InoLog.LogDatabaseFailed(Log, error, "connect-db");
            await Show(State with { Status = $"Could not connect: {error.Message}" });
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogDatabaseFailed(Log, error, "query-db");
            await Show(State with { Status = $"Could not run the query: {error.Message}" });
            return;
        }

        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            try { await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6); }
            catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "memory"); }
        }

        await Show(InoModel.DecideQueryDbSuccess(State, result, database));
    }

    [DigitalBrain.Neurons.On]
    public Task New(InoNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversationAsync();
    }

    [DigitalBrain.Neurons.On]
    public Task NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversationAsync();
    }

    private async Task StartConversationAsync()
    {
        var conversationId = InoModel.MintConversationId();
        var agentKey = InoModel.AgentKeyOf(conversationId);
        await Show(InoModel.NewConversation(State, conversationId));
        await FollowAgentControlSignalsAsync(agentKey);
        await EnsureChatAsync(agentKey);
    }

    [DigitalBrain.Neurons.On]
    public async Task Voice(InoVoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(InoModel.DecideVoice(State));
    }

    [DigitalBrain.Neurons.On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetOptional<IAppStudio>("main") is { } studio)
        {
            try { target = (await studio.Build(sentence)).Name; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                InoLog.LogOptionalUnavailable(Log, error, "app-studio");
            }
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            try { await memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6); }
            catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "memory"); }
        }
        await Show(InoModel.DecideMake(State, target, sentence, CurrentBrainId));
    }

    [DigitalBrain.Neurons.On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null || string.IsNullOrEmpty(changed.DisplayName) && string.IsNullOrEmpty(changed.DefaultModel))
        {
            return;
        }

        var settings = await Get<IUserSettings>("settings").Read();
        var smart = IsSmart(settings);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), settings, null); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "agent"); }
        if (!State.Status.StartsWith("Switched model to ", StringComparison.Ordinal))
        {
            await Show(State with { Smart = smart, Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
        }
        else
        {
            await Show(State with { Smart = smart });
        }
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
            _chatBound = true;
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _chatBound = false;
            InoLog.LogOptionalUnavailable(Log, error, "chat");
            return false;
        }
    }

    private async Task ProjectCurrentChatAsync(string agentKey)
    {
        var chatKey = InoModel.ChatKeyOf(State.ConversationId);
        var fullAgentKey = $"{CurrentBrainId}/{agentKey}";
        try
        {
            var chatState = await Get<IChat>(chatKey).Read();
            var runFailures = await ReadAgentRunFailuresAsync(agentKey, chatState);
            var next = InoModel.Projected(State, chatState, fullAgentKey, runFailures);
            if (!ReferenceEquals(next, State))
            {
                await Show(next);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "chat-read");
        }
    }

    private async Task<IReadOnlyDictionary<string, AgentFailure>?> ReadAgentRunFailuresAsync(string agentKey, ChatState chat)
    {
        var hasFailed = chat.Run?.Status == ChatRunStatus.Failed
            || (chat.Projection?.Messages.Any(m => string.Equals(m.Status, "failed", StringComparison.Ordinal)) ?? false);
        if (!hasFailed)
        {
            return null;
        }

        try
        {
            var state = await Get<IAgent>(agentKey).Read();
            var map = new Dictionary<string, AgentFailure>(StringComparer.Ordinal);
            foreach (var run in state.Runs)
            {
                if (run.Failure != AgentFailure.Unknown)
                {
                    map[run.RunId] = run.Failure;
                }
            }
            if (state.ActiveRun is { Failure: not AgentFailure.Unknown } active)
            {
                map[active.RunId] = active.Failure;
            }
            return map;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "agent-failures");
            return null;
        }
    }

    private async Task FollowAgentControlSignalsAsync(string agentKey)
    {
        try { await Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "conversations-registry"); }

        try
        {
            await Follow<IAgent, AgentBlocked>(agentKey);
            if (!_chatBound)
            {
                await Follow<IAgent, TurnCompleted>(agentKey);
                await Follow<IAgent, TurnFailed>(agentKey);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "agent-synapses"); }
    }

    private async Task EnsureProfileContextAsync()
    {
        try { await Get<IProfileContextWriter>("ino/profile-writer").Ensure(); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "profile-context"); }
    }

    private async Task<bool> IsComputeBudgetExceededAsync()
    {
        try { return (await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Read()).BudgetExceeded; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "compute");
            return false;
        }
    }

    private async Task RecordComputeAsync(string? reply)
    {
        try { await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Record("ino", Math.Max(1_000L, (reply?.Length ?? 0) * 25L)); }
        catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "compute-record"); }
    }

    private async Task<Approval[]> ReadApprovalsAsync()
    {
        try { return [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "approvals");
            return State.Approvals ?? [];
        }
    }

    private async Task<string[]> ReadGrantSummariesAsync()
    {
        try
        {
            var grants = await Get<IGrantsNeuron>(IGrantsNeuron.DefaultKey).List();
#pragma warning disable CS0618
            var assistant = DigitalBrain.Capabilities.CapabilityCallers.AssistantApp;
#pragma warning restore CS0618
            return InoModel.SummarizeGrants(grants, assistant);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "grants");
            return State.GrantSummaries ?? [];
        }
    }

    private async Task<LocalModelSummary?> DetectLocalModelsAsync()
    {
        try
        {
            var catalog = Get<IModelCatalog>(new NeuronId(new BrainId(CurrentBrainId), "ai.models", ModelsKey));
            var summary = await catalog.DetectLocal();
            return summary.Available ? summary : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            InoLog.LogOptionalUnavailable(Log, error, "local-models");
            return null;
        }
    }

    private static string? ResolveEffectiveModel(UserSettingsState settings, LocalModelSummary? local)
    {
        var explicitChoice = settings.Variables.Any(v => v.Key == ModelChosenVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));
        if (!explicitChoice
            && string.Equals(settings.DefaultModel?.Trim(), StockDefaultModel, StringComparison.OrdinalIgnoreCase)
            && local is { Available: true, PreferredModel.Length: > 0 })
        {
            return local.PreferredModel;
        }

        return string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim();
    }

    private async Task EnsureConfiguredAsync(IAgent agent, UserSettingsState settings, LocalModelSummary? local)
    {
        var effectiveSettings = ResolveEffectiveModel(settings, local) is { } preferred
            && !string.Equals(preferred, settings.DefaultModel, StringComparison.Ordinal)
            ? settings with { DefaultModel = preferred }
            : settings;

        var current = await agent.Read();
        var definition = InoAgentDefinition.For(effectiveSettings);
        if (current.Revision == 0)
        {
            try { await agent.Configure(definition, 0); }
            catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase))
            {
                InoLog.LogOptionalUnavailable(Log, error, "agent-configure-conflict");
            }
            try { await Get<IToneObserver>(InoModel.ToneKeyOf(State.ConversationId)).Follow(InoModel.AgentKeyOf(State.ConversationId)); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                InoLog.LogOptionalUnavailable(Log, error, "tone-observer");
            }
            return;
        }

        if (current.ActiveRun is null
            && (!string.Equals(current.Definition.Model?.Model, definition.Model?.Model, StringComparison.Ordinal)
                || !string.Equals(current.Definition.Instructions, definition.Instructions, StringComparison.Ordinal)))
        {
            try { await agent.Configure(definition, current.Revision); }
            catch (InvalidOperationException error)
            {
                InoLog.LogOptionalUnavailable(Log, error, "agent-reconfigure-conflict");
            }
        }
    }

    private async Task HandleSlashCommandAsync(string rawCommand, UserSettingsState settings)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "new":
            case "clear":
                await StartConversationAsync();
                return;
            case "pin":
                await Show(State with { IsPinned = !State.IsPinned, Status = State.IsPinned ? "Unpinned session" : "Pinned session to Arc bar" });
                return;
            case "rename":
                var nextTitle = string.IsNullOrWhiteSpace(arg) ? "Renamed session" : arg;
                await Show(State with { Title = nextTitle, Status = $"Renamed to '{nextTitle}'" });
                return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? "claude-sonnet-4-5" : arg.Trim();
                var settingsNeuron = Get<IUserSettings>("settings");
                await settingsNeuron.SetVariable(ModelChosenVariable, "on");
                var updated = await settingsNeuron.SetProfile(settings.DisplayName, settings.Theme, nextModel);
                await EnsureProfileContextAsync();
                try { await EnsureConfiguredAsync(Get<IAgent>(InoModel.AgentKeyOf(State.ConversationId)), updated, null); }
                catch (Exception error) when (error is not OperationCanceledException) { InoLog.LogOptionalUnavailable(Log, error, "agent"); }
                await Show(State with { Status = $"Switched model to {nextModel}", Error = null });
                return;
            case "scenario":
            case "session":
                await Show(InoView.BuildScenarioSessionState(string.IsNullOrWhiteSpace(arg) ? "q4-renewal" : arg.ToLowerInvariant(), settings));
                return;
            default:
                await Show(State with
                {
                    Status = $"Executed /{cmd}",
                    Turns = [.. State.Turns, new InoTurn(rawCommand, "Commands: `/new`, `/rename <title>`, `/pin`, `/model <id>`, `/clear`.", $"slash:{cmd}", 0)],
                });
                return;
        }
    }

    private static bool IsSmart(UserSettingsState settings)
        => settings.Variables.Any(v => v.Key == SmartVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));

    private bool IsCurrent(string agentId)
        => string.Equals(agentId, InoModel.AgentKeyOf(State.ConversationId), StringComparison.Ordinal);

    private DateTimeOffset NowUtc()
    {
        try { return Context.Time.GetUtcNow(); }
        catch (InvalidOperationException error)
        {
            InoLog.LogOptionalUnavailable(Log, error, "clock");
            return DateTimeOffset.UtcNow;
        }
    }

    private static string InferProviderFromCredential(string credential)
    {
        if (credential.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase)) { return "anthropic"; }
        if (credential.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase)) { return "openrouter"; }
        if (credential.StartsWith("AIza", StringComparison.Ordinal)) { return "google"; }
        return "openai";
    }

    private static string DefaultModelForProvider(string provider) => provider switch
    {
        "anthropic" => "claude-sonnet-4-5",
        "google" => "gemini-2.5-pro",
        "openrouter" => "openrouter/openai/gpt-4o-mini",
        _ => "gpt-5",
    };

    public new InoAppState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => InoView.Side(State);
    protected override View Render(InoAppState state) => InoView.Screen(state);

    private string CurrentBrainId
    {
        get
        {
            try { return Id.Brain.Value is { Length: > 0 } b ? b : "local"; }
            catch (InvalidOperationException error)
            {
                InoLog.LogOptionalUnavailable(Log, error, "brain-id");
                return "local";
            }
        }
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            InoLog.LogOptionalUnavailable(Log, error, typeof(T).Name);
            return null;
        }
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
