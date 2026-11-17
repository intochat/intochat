using DigitalBrain;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Anthropic;
using IntoChat.OS.Modules.AI.Models.Google;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.AI.Models.OpenRouter;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

internal sealed record InoSetupHost(
    Func<InoAppState> GetState,
    Func<InoAppState, Task> ShowState,
    Func<BrainId> GetBrain,
    Func<IModelCatalog> GetCatalog,
    Func<IUserSettings> GetSettings,
    Func<string, IAgent> GetAgent,
    Func<string, IChat> GetChat,
    Func<string, IToneObserver> GetToneObserver,
    Func<ISpeechRecognizer> GetSpeechRecognizer,
    Func<IGrantsNeuron> GetGrants,
    Func<IApprovals> GetApprovals,
    Func<IInoConversations> GetConversations,
    Func<IProfileContextWriter> GetProfileWriter,
    Func<string, Task> FollowAgentBlocked,
    Func<string, Task> FollowChatSignals,
    Func<string, Func<Task>, Task> TryOptional,
    Func<string, bool, Func<Task<bool>>, Task<bool>> ReadBoolOptional,
    Func<string, Approval[], Func<Task<Approval[]>>, Task<Approval[]>> ReadApprovalsOptional,
    Func<string, string[], Func<Task<string[]>>, Task<string[]>> ReadStringArrayOptional,
    Func<string, UserSettingsState?, Func<Task<UserSettingsState?>>, Task<UserSettingsState?>> ReadSettingsOptional,
    Func<string, LocalModelSummary?, Func<Task<LocalModelSummary?>>, Task<LocalModelSummary?>> ReadLocalOptional,
    Func<string, ModelChoices?, Func<Task<ModelChoices?>>, Task<ModelChoices?>> ReadChoicesOptional,
    Func<string, ChatState?, Func<Task<ChatState?>>, Task<ChatState?>> ReadChatOptional,
    Action<Exception, string> LogOptional,
    ILogger? Logger);

internal static class InoModelSetup
{
    public const string SmartVariable = "ino.smart";

    public static bool IsSmart(UserSettingsState s)
        => s.Variables.Any(v => v.Key == SmartVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));

    public static Task<bool> ReadCanDictateAsync(InoSetupHost host)
        => host.ReadBoolOptional("speech-recognizer", false, async () => (await host.GetSpeechRecognizer().Describe()).Available);

    public static Task<Approval[]> ReadApprovalsAsync(InoSetupHost host, Approval[]? fallback)
        => host.ReadApprovalsOptional("approvals", fallback ?? [], async () => [.. await host.GetApprovals().Pending()]);

    public static Task<string[]> ReadGrantSummariesAsync(InoSetupHost host, string[]? fallback)
        => host.ReadStringArrayOptional("grants", fallback ?? [], async () =>
        {
            var grants = await host.GetGrants().List();
            return InoModel.SummarizeGrants(grants, IntoChat.OS.Framework.CallerApps.Assistant);
        });

    public static Task EnsureProfileContextAsync(InoSetupHost host)
        => host.TryOptional("profile-context", () => host.GetProfileWriter().Ensure());

    public static async Task FollowAgentControlSignalsAsync(InoSetupHost host, string agentKey)
    {
        await host.TryOptional("conversations-registry", () => host.GetConversations().Use(agentKey));
        await host.TryOptional("agent-synapses", () => host.FollowAgentBlocked(agentKey));
    }

    public static async Task<bool> InitializeViewAsync(InoSetupHost host)
    {
        var canDictate = await ReadCanDictateAsync(host);
        var current = host.GetState();
        if (!string.IsNullOrWhiteSpace(current.SelectedModel)) { return canDictate; }

        var settings = await host.ReadSettingsOptional("settings-read", null, async () => await host.GetSettings().Read());
        if (settings is null) { return canDictate; }

        var (local, _, displayModel) = await ResolveForSettingsAsync(host, settings);
        await host.ShowState(current with
        {
            SelectedModel = displayModel,
            Smart = IsSmart(settings),
            Approvals = await ReadApprovalsAsync(host, current.Approvals),
            GrantSummaries = await ReadGrantSummariesAsync(host, current.GrantSummaries),
            DetectedLocalModel = local?.PreferredModel ?? current.DetectedLocalModel,
            DetectedLocalModels = local?.InstalledModels ?? current.DetectedLocalModels,
        });
        return canDictate;
    }

    public static async Task<bool> EnsureChatAsync(InoSetupHost host, string agentKey)
    {
        var state = host.GetState();
        var chatKey = InoModel.ChatKeyOf(state.ConversationId);
        var fullAgentKey = $"{host.GetBrain().Value}/{agentKey}";
        try
        {
            var chat = host.GetChat(chatKey);
            var current = await chat.Read();
            if (!string.Equals(current.Selection?.Agent, fullAgentKey, StringComparison.Ordinal))
            {
                await chat.SelectAgent(fullAgentKey, current.Selection?.Revision ?? 0, Guid.NewGuid());
            }
            await host.FollowChatSignals(chatKey);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            host.LogOptional(error, "chat");
            return false;
        }
    }

    public static async Task ProjectCurrentChatAsync(InoSetupHost host, string agentKey)
    {
        var state = host.GetState();
        var chatState = await host.ReadChatOptional("chat-read", null, () => host.GetChat(InoModel.ChatKeyOf(state.ConversationId)).Read()!);
        if (chatState is null) { return; }
        var next = InoModel.Projected(state, chatState, $"{host.GetBrain().Value}/{agentKey}");
        if (next.Error?.Code == "provider-unavailable" && await IsMissingLocalOllamaAsync(host, null))
        {
            next = next with { Error = new InoError("no-provider") };
        }
        if (!ReferenceEquals(next, state)) { await host.ShowState(next); }
    }

    public static async Task ConfigureOpenAgentAsync(InoSetupHost host, string agentKey, UserSettingsState settings, string? effectiveModel, bool migrate)
    {
        await EnsureProfileContextAsync(host);
        var agent = host.GetAgent(agentKey);
        if (migrate)
        {
            await agent.Post(InoModel.MigrationNote, InoModel.MigrationId);
            await host.ShowState(host.GetState() with { AgentKey = agentKey });
        }
        await EnsureConfiguredAsync(host, agent, settings, effectiveModel, host.GetState().ConversationId);
        await FollowAgentControlSignalsAsync(host, agentKey);
        var read = await agent.Read();
        if (read.Blocked) { await host.ShowState(InoModel.Pause(host.GetState(), host.GetState().BlockedReason)); }
        else if (host.GetState().Blocked) { await host.ShowState(InoModel.Resume(host.GetState())); }
    }

    public static async Task ExecuteTurnAsync(InoSetupHost host, UserSettingsState settings, InoPending pending)
    {
        var state = host.GetState();
        var agentKey = InoModel.AgentKeyOf(state.ConversationId);
        var (local, effectiveModel, displayModel) = await ResolveForSettingsAsync(host, settings);
        if (local is not null && (state.DetectedLocalModel != local.PreferredModel || state.DetectedLocalModels is null))
        {
            await host.ShowState(host.GetState() with { DetectedLocalModel = local.PreferredModel, DetectedLocalModels = local.InstalledModels });
        }

        await EnsureChatAsync(host, agentKey);
        try
        {
            await EnsureProfileContextAsync(host);
            var agent = host.GetAgent(agentKey);
            await EnsureConfiguredAsync(host, agent, settings, effectiveModel, host.GetState().ConversationId);
            await FollowAgentControlSignalsAsync(host, agentKey);
            var runId = await agent.Start(AgentTurn.Text(pending.Text, model: effectiveModel));
            await host.ShowState(InoModel.Started(host.GetState(), runId, displayModel));
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            await host.ShowState(InoModel.StartFailed(host.GetState(), new InoError("paused", error.Message[InoModel.BlockedPrefix.Length..])));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (host.Logger is { } log) { InoLog.LogTurnStartFailed(log, error, agentKey); }
            var code = AgentFailures.CodeOf(AgentFailures.Classify(error));
            if (code == "provider-unavailable" && await IsMissingLocalOllamaAsync(host, local)) { code = "no-provider"; }
            await host.ShowState(InoModel.StartFailed(host.GetState(), new InoError(code)));
        }
    }

    public static async Task<bool> ApplyLocalModelAsync(InoSetupHost host, InoUseLocalModel request)
    {
        var local = await DetectLocalModelsAsync(host);
        var state = host.GetState();
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim()
            : !string.IsNullOrWhiteSpace(request.Value) ? request.Value.Trim() : local?.PreferredModel ?? state.DetectedLocalModel ?? ModelCatalog.DefaultChatModelId;
        await PersistDefaultModelAsync(host, model, state.ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), model, $"Switched model to {model}", local));
        return retry;
    }

    public static async Task<bool> SaveProviderAsync(InoSetupHost host, InoSaveProvider request)
    {
        if (request.Value?.Trim() is not { Length: > 0 } credential) { return false; }
        var provider = !string.IsNullOrWhiteSpace(request.Provider) ? request.Provider.Trim().ToLowerInvariant() : InferProviderFromCredential(credential);
        try { await host.GetCatalog().SaveProviderCredential(provider, credential); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (host.Logger is { } log) { InoLog.LogProviderSaveFailed(log, error, provider); }
            await host.ShowState(host.GetState() with { Error = new InoError("unknown"), Status = InoModel.Failed });
            return false;
        }
        var defaultModel = DefaultModelForProvider(provider);
        await PersistDefaultModelAsync(host, defaultModel, host.GetState().ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), defaultModel, $"Saved {provider} credential · Ready on {defaultModel}"));
        return retry;
    }

    public static async Task SyncSettingsAsync(InoSetupHost host, UserSettingsChanged changed)
    {
        var settings = await host.GetSettings().Read();
        var (_, effectiveModel, displayModel) = await ResolveForSettingsAsync(host, settings);
        await EnsureProfileContextAsync(host);
        var conversationId = host.GetState().ConversationId;
        await host.TryOptional("agent", () => EnsureConfiguredAsync(host, host.GetAgent(InoModel.AgentKeyOf(conversationId)), settings, effectiveModel, conversationId));
        await host.ShowState(InoModel.SyncedSettings(host.GetState(), IsSmart(settings), displayModel, changed.DisplayName, displayModel));
    }

    public static async Task HandleSlashCommandAsync(InoSetupHost host, string rawCommand, UserSettingsState settings, Func<Task> startConversation)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;
        var state = host.GetState();
        switch (cmd)
        {
            case "new" or "clear": await startConversation(); return;
            case "pin": await host.ShowState(InoModel.DecideSlashPin(state)); return;
            case "rename": await host.ShowState(InoModel.DecideSlashRename(state, arg)); return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? ModelCatalog.DefaultChatModelId : arg.Trim();
                await PersistDefaultModelAsync(host, nextModel, state.ConversationId);
                await host.ShowState(InoModel.ModelSelected(host.GetState(), nextModel, $"Switched model to {nextModel}"));
                return;
            case "scenario" or "session": await host.ShowState(InoModel.DecideScenario(arg, settings)); return;
            default: await host.ShowState(InoModel.DecideSlashHelp(state, rawCommand, cmd)); return;
        }
    }

    public static Task<LocalModelSummary?> DetectLocalModelsAsync(InoSetupHost host)
        => host.ReadLocalOptional("local-models", null, async () =>
        {
            var summary = await host.GetCatalog().DetectLocal();
            return summary.Available ? summary : null;
        });

    public static Task<ModelChoices?> ReadModelChoicesAsync(InoSetupHost host)
        => host.ReadChoicesOptional("model-catalog", null, async () => await host.GetCatalog().List(null));

    public static async Task<(LocalModelSummary? Local, string? EffectiveModel, string DisplayModel)> ResolveForSettingsAsync(
        InoSetupHost host,
        UserSettingsState settings)
    {
        var local = await DetectLocalModelsAsync(host);
        var (effective, display) = await ResolveEffectiveModelAsync(host, settings, local);
        return (local, effective, display);
    }

    public static async Task<(string? EffectiveModel, string DisplayModel)> ResolveEffectiveModelAsync(
        InoSetupHost host,
        UserSettingsState settings,
        LocalModelSummary? local)
    {
        var personModel = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim();
        if (personModel is not null) { return (personModel, personModel); }

        var choices = await ReadModelChoicesAsync(host);
        if (choices is not null)
        {
            var auto = choices.Automatic;
            if (!string.IsNullOrWhiteSpace(auto.Model) && !string.IsNullOrWhiteSpace(auto.Provider)) { return (auto.Model, auto.Model); }
            var fallbackDisplay = local?.PreferredModel is { Length: > 0 } p ? p : ModelCatalog.DefaultChatModelId;
            return (null, fallbackDisplay);
        }

        var fallback = local?.PreferredModel is { Length: > 0 } detected ? detected : ModelCatalog.DefaultChatModelId;
        return (fallback, fallback);
    }

    public static async Task<bool> IsMissingLocalOllamaAsync(InoSetupHost host, LocalModelSummary? local)
    {
        var choices = await ReadModelChoicesAsync(host);
        if (choices is null || !string.Equals(choices.Automatic.Provider, AiProvider.Ollama.ToString(), StringComparison.OrdinalIgnoreCase)) { return false; }
        if (choices.Models.Any(m => m.Available && !string.Equals(m.Provider, AiProvider.Ollama.ToString(), StringComparison.OrdinalIgnoreCase))) { return false; }
        var detected = local ?? await DetectLocalModelsAsync(host);
        return detected is not { Available: true };
    }

    public static async Task EnsureConfiguredAsync(
        InoSetupHost host,
        IAgent agent,
        UserSettingsState settings,
        string? effectiveModel,
        string conversationId)
    {
        var effectiveSettings = string.IsNullOrWhiteSpace(effectiveModel)
            ? settings
            : settings with { DefaultModel = effectiveModel.Trim() };

        var current = await agent.Read();
        var definition = InoAgentDefinition.For(effectiveSettings);
        if (current.Revision == 0)
        {
            await host.TryOptional("agent-configure-conflict", () => agent.Configure(definition, 0));
            await host.TryOptional("tone-observer", () => host.GetToneObserver(InoModel.ToneKeyOf(conversationId)).Follow(InoModel.AgentKeyOf(conversationId)));
            return;
        }

        if (current.ActiveRun is null
            && (!string.Equals(current.Definition.Model?.Model, definition.Model?.Model, StringComparison.Ordinal)
                || !string.Equals(current.Definition.Instructions, definition.Instructions, StringComparison.Ordinal)))
        {
            await host.TryOptional("agent-reconfigure-conflict", () => agent.Configure(definition, current.Revision));
        }
    }

    public static async Task PersistDefaultModelAsync(InoSetupHost host, string model, string conversationId)
    {
        var settingsNeuron = host.GetSettings();
        var current = await settingsNeuron.Read();
        var updated = await settingsNeuron.SetProfile(current.DisplayName, current.Theme, model);
        await EnsureProfileContextAsync(host);
        await host.TryOptional("agent", () => EnsureConfiguredAsync(host, host.GetAgent(InoModel.AgentKeyOf(conversationId)), updated, model, conversationId));
    }

    public static string InferProviderFromCredential(string credential)
        => credential.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) ? "anthropic"
            : credential.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase) ? "openrouter"
            : credential.StartsWith("AIza", StringComparison.Ordinal) ? "google" : "openai";

    public static string DefaultModelForProvider(string provider) => provider switch
    {
        "anthropic" => CatalogId(typeof(ISonnet46)),
        "google" => CatalogId(typeof(IGemini36Pro)),
        "openrouter" => CatalogId(typeof(IDeepSeekV41Flash)),
        _ => CatalogId(typeof(IGpt54)),
    };

    private static string CatalogId(Type marker)
        => LLMModel.FindByMarker(marker)?.Id ?? ModelCatalog.DefaultChatModelId;

    public static AppBundle BuildBundle(Type compiledType) => new(
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
        CompiledType = compiledType.AssemblyQualifiedName,
    };
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
