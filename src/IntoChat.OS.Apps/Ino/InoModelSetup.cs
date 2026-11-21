using DigitalBrain;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.UI.Chat;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Apps.Ino;

internal sealed record InoCatalogSnapshot(ModelId? PickedChat, ModelCard[] Cards, ModelAvailability[] Availability);

internal sealed record InoSetupHost(
    Func<InoAppState> GetState,
    Func<InoAppState, Task> ShowState,
    Func<BrainId> GetBrain,
    Func<IModelCatalog> GetCatalog,
    Func<IDevice?> GetDevice,
    Func<IUserSettings> GetSettings,
    Func<string, IAgent> GetAgent,
    Func<string, IChat> GetChat,
    Func<string, IToneObserver> GetToneObserver,
    Func<ISpeechRecognizer> GetSpeechRecognizer,
    Func<IGrantsNeuron> GetGrants,
    Func<IApprovals> GetApprovals,
    Func<IInoConversations> GetConversations,
    Func<IProfileContextWriter> GetProfileWriter,
    Func<string, IAgentContext> GetAgentContext,
    Func<string, IChatThread> GetUiChatThread,
    Func<string, Task> FollowAgentBlocked,
    Func<string, Task> FollowChatSignals,
    Func<string, Task> FollowUiChatSignals,
    Func<string, Func<Task>, Task> TryOptional,
    Func<string, bool, Func<Task<bool>>, Task<bool>> ReadBoolOptional,
    Func<string, Approval[], Func<Task<Approval[]>>, Task<Approval[]>> ReadApprovalsOptional,
    Func<string, string[], Func<Task<string[]>>, Task<string[]>> ReadStringArrayOptional,
    Func<string, UserSettingsState?, Func<Task<UserSettingsState?>>, Task<UserSettingsState?>> ReadSettingsOptional,
    Func<string, DeviceView?, Func<Task<DeviceView?>>, Task<DeviceView?>> ReadDeviceOptional,
    Func<string, InoCatalogSnapshot?, Func<Task<InoCatalogSnapshot?>>, Task<InoCatalogSnapshot?>> ReadCatalogOptional,
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
            return InoModel.SummarizeGrants(grants, IntoChat.OS.Modules.Identity.Contracts.Grants.CallerApps.Assistant);
        });

    public static Task<string[]> ReadRecentConversationsAsync(InoSetupHost host, string[]? fallback = null)
        => host.ReadStringArrayOptional("conversations-list", fallback ?? [], () => host.GetConversations().List());

    public static async Task<long> ReadLastRunTokensAsync(InoSetupHost host, string agentKey, long fallback)
    {
        try
        {
            var agentState = await host.GetAgent(agentKey).Read();
            var lastUsage = agentState.Runs.LastOrDefault(static r => r.Usage is not null)?.Usage;
            if (lastUsage is not null)
            {
                var total = lastUsage.TotalTokens ?? ((lastUsage.InputTokens ?? 0L) + (lastUsage.OutputTokens ?? 0L));
                if (total > 0) { return total; }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            host.LogOptional(error, "agent-usage");
        }
        return fallback;
    }

    public static async Task SyncUiChatThreadAsync(InoSetupHost host, InoAppState state, bool canDictate)
    {
        await host.TryOptional("ui-chat-sync", async () =>
        {
            var uiKey = InoModel.UiChatKeyOf(state.ConversationId);
            var thread = host.GetUiChatThread(uiKey);
            var activeModel = state.SelectedModel ?? KnownModels.Defaults.Device[Role.Chat].Value;
            var catalogCards = await host.ReadCatalogOptional("ui-chat-models", null, async () =>
            {
                var cards = await host.GetCatalog().List(Abilities.Text);
                return new InoCatalogSnapshot(null, cards, []);
            });
            ChatModelOption[] modelOptions = catalogCards is { Cards.Length: > 0 }
                ? [.. catalogCards.Cards.Select(static card => new ChatModelOption(card.Id.Value, card.Name, card.Provider.ToString()))]
                : [new ChatModelOption(activeModel, activeModel)];

            await thread.Configure(state.ConversationId, state.Title, activeModel, modelOptions, voiceAvailable: canDictate);

            foreach (var msg in InoView.Shown(state))
            {
                var toolCalls = msg.Activities.Select(static a => new ChatToolCall(
                    a.Id,
                    a.Title,
                    "{}",
                    ResultJson: a.Error ?? a.Result,
                    Status: string.IsNullOrWhiteSpace(a.Error) ? "done" : "failed")).ToArray();

                var slots = msg.Cards
                    .Where(static c => string.Equals(c.Kind, "chart", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.SourceNeuronUri))
                    .Select(static c => new ChatInlineSlot("ui.chart", InoView.StripNeuronUri(c.SourceNeuronUri)))
                    .ToArray();

                await thread.AppendMessage(new ChatMessage(
                    msg.Id,
                    msg.Role,
                    msg.Text ?? string.Empty,
                    ToolCalls: toolCalls.Length > 0 ? toolCalls : null,
                    Slots: slots.Length > 0 ? slots : null,
                    Status: msg.Status,
                    Error: msg.Failure));
            }

            await thread.SetWorking(state.ActiveRunId is not null || state.Pending is not null, state.Error?.Detail ?? state.Error?.Code);

            long totalIn = 0, totalOut = 0;
            string? summary = state.ContextSummary;
            try
            {
                var agentState = await host.GetAgent(InoModel.AgentKeyOf(state.ConversationId)).Read();
                foreach (var run in agentState.Runs)
                {
                    if (run.Usage is { } u)
                    {
                        totalIn += u.InputTokens ?? 0L;
                        totalOut += u.OutputTokens ?? 0L;
                    }
                }
                if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(agentState.Summary))
                {
                    summary = agentState.Summary;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                host.LogOptional(error, "ui-chat-agent-usage");
            }

            List<ChatContextItem> contextItems = [];
            try
            {
                var snap = await host.GetAgentContext("default").Read();
                foreach (var entry in snap.Entries.Take(24))
                {
                    var preview = entry.Text.Length > 120 ? entry.Text[..117] + "..." : entry.Text;
                    contextItems.Add(new ChatContextItem(
                        entry.Name,
                        preview,
                        entry.Provenance?.Trust ?? ContextTrust.Platform,
                        Math.Max(1, entry.Text.Length / 4)));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                host.LogOptional(error, "ui-chat-context");
            }

            await thread.SetContext(contextItems, summary, totalIn, totalOut);
        });
    }

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

        var (_, _, displayModel) = await ResolveForSettingsAsync(host, settings);
        await host.ShowState(current with
        {
            SelectedModel = displayModel,
            Smart = IsSmart(settings),
            Approvals = await ReadApprovalsAsync(host, current.Approvals),
            GrantSummaries = await ReadGrantSummariesAsync(host, current.GrantSummaries),
            RecentConversations = await ReadRecentConversationsAsync(host, current.RecentConversations),
        });
        return canDictate;
    }

    private static string FullAgentKey(BrainId brain, string agentKey)
        => brain.Value is { Length: > 0 }
            ? new NeuronId(brain, AgentSignals.NeuronType, agentKey).FullKey()
            : $"/{agentKey}";

    public static async Task<bool> EnsureChatAsync(InoSetupHost host, string agentKey)
    {
        var state = host.GetState();
        var chatKey = InoModel.ChatKeyOf(state.ConversationId);
        var fullAgentKey = FullAgentKey(host.GetBrain(), agentKey);
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
        var next = InoModel.Projected(state, chatState, FullAgentKey(host.GetBrain(), agentKey));
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
        var (device, resolvedEffective, resolvedDisplay) = await ResolveForSettingsAsync(host, settings);
        var selectedTrimmed = string.IsNullOrWhiteSpace(state.SelectedModel) ? null : state.SelectedModel.Trim();
        var hasConversationOverride = selectedTrimmed is not null
            && !string.Equals(selectedTrimmed, resolvedDisplay, StringComparison.Ordinal);
        var effectiveModel = hasConversationOverride ? selectedTrimmed : resolvedEffective;
        var displayModel = hasConversationOverride ? selectedTrimmed! : resolvedDisplay;

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
            if (code == "provider-unavailable" && await IsMissingLocalOllamaAsync(host, device)) { code = "no-provider"; }
            await host.ShowState(InoModel.StartFailed(host.GetState(), new InoError(code)));
        }
    }

    public static async Task<bool> ApplyLocalModelAsync(InoSetupHost host, InoUseLocalModel request)
    {
        var device = await ReadDeviceAsync(host);
        var state = host.GetState();
        var preferred = PreferredLocalModel(device);
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim()
            : !string.IsNullOrWhiteSpace(request.Value) ? request.Value.Trim() : preferred ?? KnownModels.Defaults.Device[Role.Chat].Value;
        var modelId = KnownModels.Find(model)?.Id ?? new ModelId(model);
        await SetChatRoleAsync(host, modelId);
        await ReconfigureAgentAsync(host, modelId.Value, state.ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), modelId.Value, $"Switched model to {modelId.Value}"));
        return retry;
    }

    public static async Task ChooseModelAsync(InoSetupHost host, string model, bool makeDefault)
    {
        var trimmed = model.Trim();
        var modelId = KnownModels.Find(trimmed)?.Id ?? new ModelId(trimmed);
        var state = host.GetState();
        if (makeDefault)
        {
            await SetChatRoleAsync(host, modelId);
        }

        await ReconfigureAgentAsync(host, modelId.Value, state.ConversationId);
        var label = KnownModels.Find(modelId.Value)?.Name ?? modelId.Value;
        var status = makeDefault ? $"Default Chat model set to {label}" : $"Switched conversation to {label}";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), modelId.Value, status));
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
        await SetChatRoleAsync(host, defaultModel);
        await ReconfigureAgentAsync(host, defaultModel.Value, host.GetState().ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), defaultModel.Value, $"Saved {provider} credential · Ready on {defaultModel.Value}"));
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
            case "compact":
                var messages = state.Messages ?? [];
                AgentCompactionResult? agentCompacted = null;
                try
                {
                    agentCompacted = await host.GetAgent(InoModel.AgentKeyOf(state.ConversationId)).Compact(string.IsNullOrWhiteSpace(arg) ? null : arg);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    host.LogOptional(error, "agent-compact");
                }

                if (messages.Length == 0 && (agentCompacted is null || agentCompacted.PreviousMessageCount == 0))
                {
                    await host.ShowState(state with { Status = "No messages to compact yet." });
                    return;
                }

                var summary = agentCompacted is { Summary.Length: > 0 }
                    ? agentCompacted.Summary
                    : InoModel.BuildCompactSummary(messages, state.ContextSummary);
                await host.TryOptional("compact-context", () => host.GetAgentContext("context/conversation.summary").Upsert(
                    new ContextEntry("summary", "Earlier conversation summary: " + summary, [])));
                var compactedCount = agentCompacted is { PreviousMessageCount: > 0 }
                    ? agentCompacted.PreviousMessageCount - agentCompacted.KeptMessageCount
                    : messages.Length;
                await host.ShowState(state with
                {
                    ContextSummary = summary,
                    Status = $"Compacted {compactedCount} message(s) into conversation summary.",
                });
                return;
            case "pin": await host.ShowState(InoModel.DecideSlashPin(state)); return;
            case "rename": await host.ShowState(InoModel.DecideSlashRename(state, arg)); return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? KnownModels.Defaults.Device[Role.Chat].Value : arg.Trim();
                await ChooseModelAsync(host, nextModel, makeDefault: true);
                return;
            case "scenario" or "session": await host.ShowState(InoModel.DecideScenario(arg, settings)); return;
            default: await host.ShowState(InoModel.DecideSlashHelp(state, rawCommand, cmd)); return;
        }
    }

    public static Task<DeviceView?> ReadDeviceAsync(InoSetupHost host)
        => host.ReadDeviceOptional("device-read", null, async () =>
        {
            var device = host.GetDevice();
            return device is null ? null : await device.Read();
        });

    public static string[] InstalledLocalModels(DeviceView? device)
        => device?.Models is { Length: > 0 } models
            ? [.. models
                .Where(static m => m.State is InstallState.Ready or InstallState.Loaded && m.Card.Can.HasFlag(Abilities.Text))
                .Select(static m => m.Card.Id.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];

    public static string? PreferredLocalModel(DeviceView? device)
    {
        var installed = InstalledLocalModels(device);
        return installed.Length > 0 ? installed[0] : null;
    }

    public static async Task<(ModelCard[] Cards, ModelAvailability[] Availability)?> ReadPickerAsync(InoSetupHost host)
    {
        var snapshot = await ReadCatalogSnapshotAsync(host);
        return snapshot is null ? null : (snapshot.Cards, snapshot.Availability);
    }

    public static Task<InoCatalogSnapshot?> ReadCatalogSnapshotAsync(InoSetupHost host)
        => host.ReadCatalogOptional("model-catalog", null, async () =>
        {
            var catalog = host.GetCatalog();
            var cards = await catalog.List(Abilities.Text);
            var availability = await catalog.Availability();
            ModelId? picked = null;
            try
            {
                var chosen = await catalog.Pick(Role.Chat);
                if (!string.IsNullOrWhiteSpace(chosen.Value))
                {
                    picked = chosen;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                host.LogOptional(error, "model-catalog-pick");
            }

            return new InoCatalogSnapshot(picked, cards, availability);
        });

    public static async Task<(DeviceView? Device, string? EffectiveModel, string DisplayModel)> ResolveForSettingsAsync(
        InoSetupHost host,
        UserSettingsState settings)
    {
        var device = await ReadDeviceAsync(host);
        var (effective, display) = await ResolveEffectiveModelAsync(host, settings, device);
        return (device, effective, display);
    }

    public static async Task<(string? EffectiveModel, string DisplayModel)> ResolveEffectiveModelAsync(
        InoSetupHost host,
        UserSettingsState settings,
        DeviceView? device)
    {
        var personModel = settings.RoleOf(Role.Chat)?.Value;
        if (!string.IsNullOrWhiteSpace(personModel)) { return (personModel, personModel); }

        var preferredLocal = PreferredLocalModel(device);
        var snapshot = await ReadCatalogSnapshotAsync(host);
        if (snapshot is not null)
        {
            if (snapshot.PickedChat is { Value.Length: > 0 } picked)
            {
                return (picked.Value, picked.Value);
            }
            var fallbackDisplay = preferredLocal is { Length: > 0 } p ? p : KnownModels.Defaults.Device[Role.Chat].Value;
            return (null, fallbackDisplay);
        }

        var fallback = preferredLocal is { Length: > 0 } detected ? detected : KnownModels.Defaults.Device[Role.Chat].Value;
        return (fallback, fallback);
    }

    public static async Task<bool> IsMissingLocalOllamaAsync(InoSetupHost host, DeviceView? device)
    {
        var snapshot = await ReadCatalogSnapshotAsync(host);
        if (snapshot?.PickedChat is not { Value.Length: > 0 } picked) { return false; }

        var pickedCard = snapshot.Cards.FirstOrDefault(c => string.Equals(c.Id.Value, picked.Value, StringComparison.OrdinalIgnoreCase))
            ?? KnownModels.Find(picked.Value);
        if (pickedCard is not { Provider: AiProvider.Ollama }) { return false; }

        var readyIds = snapshot.Availability
            .Where(a => a.Ready)
            .Select(a => a.Model.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (snapshot.Cards.Any(c => c.Provider != AiProvider.Ollama && c.Can.HasFlag(Abilities.Tools) && readyIds.Contains(c.Id.Value)))
        {
            return false;
        }

        var resolvedDevice = device ?? await ReadDeviceAsync(host);
        if (resolvedDevice is not null)
        {
            var ollamaReachable = resolvedDevice.Runtimes.Any(static r => r.Id == RuntimeId.Ollama && r.Reachable);
            var hasInstalledOllama = resolvedDevice.Models.Any(static m =>
                m.Runtime == RuntimeId.Ollama && m.State is InstallState.Ready or InstallState.Loaded);
            return !(ollamaReachable && hasInstalledOllama);
        }

        return !readyIds.Contains(picked.Value);
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

    private static async Task SetChatRoleAsync(InoSetupHost host, ModelId model)
    {
        var persisted = false;
        await host.TryOptional("model-catalog-set-role", async () =>
        {
            await host.GetCatalog().SetRole(Role.Chat, model);
            persisted = true;
        });
        if (!persisted)
        {
            await host.TryOptional("settings-set-role", () => host.GetSettings().SetRole(Role.Chat, model));
        }
    }

    private static async Task ReconfigureAgentAsync(InoSetupHost host, string model, string conversationId)
    {
        var settings = await host.ReadSettingsOptional("settings-read", null, async () => await host.GetSettings().Read());
        await EnsureProfileContextAsync(host);
        if (settings is not null)
        {
            await host.TryOptional("agent", () => EnsureConfiguredAsync(host, host.GetAgent(InoModel.AgentKeyOf(conversationId)), settings, model, conversationId));
        }
    }

    public static string InferProviderFromCredential(string credential)
        => credential.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) ? "anthropic"
            : credential.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase) ? "openrouter"
            : credential.StartsWith("AIza", StringComparison.Ordinal) ? "google" : "openai";

    public static ModelId DefaultModelForProvider(string provider)
    {
        var parsed = Enum.TryParse<AiProvider>(provider, ignoreCase: true, out var p) ? p : AiProvider.OpenAI;
        var card = KnownModels.All.FirstOrDefault(c => c.Provider == parsed && c.Can.HasFlag(Abilities.Tools) && c.Tier == Tier.Balanced)
            ?? KnownModels.All.FirstOrDefault(c => c.Provider == parsed && c.Can.HasFlag(Abilities.Tools))
            ?? KnownModels.Gpt54;
        return card.Id;
    }

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
                new("choose-model", "Switch the conversation model, and optionally pin it as the Chat default."),
                new("save-provider", "Save a cloud provider credential to the brain's Secure store."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("switch-conversation", "Switch to an existing conversation."),
                new("toggle-devui", "Toggle native Modules.UI DevUI chat view."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
                new("link", "Post a pasted workspace link: its app, opaque input and text."),
                new(IntoChat.OS.UI.UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
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
