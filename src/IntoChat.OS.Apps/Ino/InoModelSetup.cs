using DigitalBrain;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Ino;

public sealed record VoiceAbilities(bool Hear, bool Speak, bool Talk)
{
    public static VoiceAbilities None { get; } = new(false, false, false);
}

internal sealed record InoCatalogSnapshot(ModelId? PickedChat, ModelCard[] Cards, ModelAvailability[] Availability);

internal sealed record InoRuntimeSnapshot(InoCatalogSnapshot? Catalog, DeviceView? Device);

internal sealed record InoSetupHost(
    Func<InoAppState> GetState,
    Func<InoAppState, Task> ShowState,
    Func<BrainId> GetBrain,
    Func<IModelCatalog> GetCatalog,
    Func<IDevice?> GetDevice,
    Func<IUserSettings> GetSettings,
    Func<string, IConversation> GetConversation,
    Func<IThreadDirectory> GetSessions,
    Func<IGrantsNeuron> GetGrants,
    Func<IApprovals> GetApprovals,
    Func<IProfileContextWriter> GetProfileWriter,
    Func<string, Task> FollowConversation,
    Func<bool, Task> RefreshConversation,
    Func<string, Func<Task>, Task> TryOptional,
    Func<string, bool, Func<Task<bool>>, Task<bool>> ReadBoolOptional,
    Func<string, Approval[], Func<Task<Approval[]>>, Task<Approval[]>> ReadApprovalsOptional,
    Func<string, string[], Func<Task<string[]>>, Task<string[]>> ReadStringArrayOptional,
    Func<string, UserSettingsState?, Func<Task<UserSettingsState?>>, Task<UserSettingsState?>> ReadSettingsOptional,
    Func<string, DeviceView?, Func<Task<DeviceView?>>, Task<DeviceView?>> ReadDeviceOptional,
    Func<string, InoCatalogSnapshot?, Func<Task<InoCatalogSnapshot?>>, Task<InoCatalogSnapshot?>> ReadCatalogOptional,
    Func<string, ThreadPage?, Func<Task<ThreadPage?>>, Task<ThreadPage?>> ReadSessionPageOptional,
    Action<Exception, string> LogOptional,
    ILogger? Logger);

internal static class InoModelSetup
{
    public const string SmartVariable = "ino.smart";

    public static bool IsSmart(UserSettingsState s)
        => s.Variables.Any(v => v.Key == SmartVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));

    public static async Task<VoiceAbilities> ReadVoiceAsync(InoSetupHost host, ModelAvailability[]? cachedAvailability = null)
    {
        try
        {
            var catalog = host.GetCatalog();
            var availability = cachedAvailability ?? await catalog.Availability();
            var ready = availability
                .Where(static a => a.Ready)
                .Select(static a => a.Model.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (ready.Count == 0)
            {
                return VoiceAbilities.None;
            }

            var hear = await IsRoleReadyAsync(host, catalog, ModelRole.Hear, ready);
            var speak = await IsRoleReadyAsync(host, catalog, ModelRole.Speak, ready);
            var talkBoundReady = await IsRoleReadyAsync(host, catalog, ModelRole.Talk, ready);
            return new VoiceAbilities(hear, speak, talkBoundReady || (hear && speak));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            host.LogOptional(error, "voice-abilities");
            return VoiceAbilities.None;
        }
    }

    private static async Task<bool> IsRoleReadyAsync(
        InoSetupHost host,
        IModelCatalog catalog,
        ModelRole role,
        HashSet<string> ready)
    {
        try
        {
            var picked = await catalog.Pick(role);
            return picked is { Value.Length: > 0 } && ready.Contains(picked.Value);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            host.LogOptional(error, $"voice-role-{role}");
            return false;
        }
    }

    public static Task<Approval[]> ReadApprovalsAsync(InoSetupHost host, Approval[]? fallback)
        => host.ReadApprovalsOptional("approvals", fallback ?? [], async () => [.. await host.GetApprovals().Pending()]);

    public static Task<string[]> ReadGrantSummariesAsync(InoSetupHost host, string[]? fallback)
        => host.ReadStringArrayOptional("grants", fallback ?? [], async () =>
        {
            var grants = await host.GetGrants().List();
            return InoModel.SummarizeGrants(grants, CallerApps.Assistant);
        });

    public static Task<ThreadPage?> ReadSessionsAsync(InoSetupHost host, int take = 20)
        => host.ReadSessionPageOptional("sessions-list", null, async () => await host.GetSessions().List(archived: false, skip: 0, take: take));

    public static Task<string[]> ReadRecentConversationsAsync(InoSetupHost host, string[]? fallback = null)
        => host.ReadStringArrayOptional("sessions-recent", fallback ?? [], async () =>
        {
            var page = await host.GetSessions().List(archived: false, skip: 0, take: InoAppState.MaxRecentConversations);
            return [.. page.Entries.Select(static e => e.Id)];
        });

    public static Task OpenSessionAsync(InoSetupHost host, string conversationId)
        => Threads.IsLocalId(conversationId)
            ? host.TryOptional("session-open", () => host.GetSessions().Open(conversationId))
            : Task.CompletedTask;

    public static Task EnsureProfileContextAsync(InoSetupHost host)
        => host.TryOptional("profile-context", () => host.GetProfileWriter().Ensure());

    public static async Task<VoiceAbilities> InitializeViewAsync(
        InoSetupHost host,
        InoRuntimeSnapshot? snapshot = null,
        UserSettingsState? cachedSettings = null)
    {
        var voice = await ReadVoiceAsync(host, snapshot?.Catalog?.Availability);
        var current = host.GetState();
        if (!string.IsNullOrWhiteSpace(current.SelectedModel)) { return voice; }

        var settings = cachedSettings ?? await host.ReadSettingsOptional("settings-read", null, async () => await host.GetSettings().Read());
        if (settings is null) { return voice; }

        var (_, _, displayModel) = await ResolveForSettingsAsync(host, settings, snapshot);
        await host.ShowState(current with
        {
            SelectedModel = displayModel,
            Smart = IsSmart(settings),
            Approvals = await ReadApprovalsAsync(host, current.Approvals),
            GrantSummaries = await ReadGrantSummariesAsync(host, current.GrantSummaries),
            RecentConversations = await ReadRecentConversationsAsync(host, current.RecentConversations),
        });
        return voice;
    }

    public static async Task ConfigureOpenConversationAsync(
        InoSetupHost host,
        string conversationId,
        UserSettingsState settings,
        string? effectiveModel,
        bool migrate)
    {
        await EnsureProfileContextAsync(host);
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        var conversation = host.GetConversation(conversationKey);
        if (migrate)
        {
            await conversation.Post(InoModel.MigrationNote, InoModel.MigrationId);
            await host.ShowState(host.GetState() with { AgentKey = InoModel.AgentKeyOf(conversationId) });
        }
        await EnsureSeatedAsync(host, conversationId, settings, effectiveModel);
        await OpenSessionAsync(host, conversationId);
        await host.TryOptional("conversation-synapses", () => host.FollowConversation(conversationKey));
    }

    public static async Task ExecuteTurnAsync(InoSetupHost host, UserSettingsState settings, InoPending pending)
    {
        var state = host.GetState();
        var conversationKey = InoModel.ConversationKeyOf(state.ConversationId);
        var (device, resolvedEffective, resolvedDisplay) = await ResolveForSettingsAsync(host, settings);
        var selectedTrimmed = string.IsNullOrWhiteSpace(state.SelectedModel) ? null : state.SelectedModel.Trim();
        var hasConversationOverride = selectedTrimmed is not null
            && !string.Equals(selectedTrimmed, resolvedDisplay, StringComparison.Ordinal);
        var effectiveModel = hasConversationOverride ? selectedTrimmed : resolvedEffective;
        var displayModel = hasConversationOverride ? selectedTrimmed! : resolvedDisplay;

        try
        {
            await EnsureProfileContextAsync(host);
            var conversation = host.GetConversation(conversationKey);
            await EnsureSeatedAsync(host, state.ConversationId, settings, effectiveModel);
            await OpenSessionAsync(host, state.ConversationId);
            await host.TryOptional("conversation-synapses", () => host.FollowConversation(conversationKey));

            ModelId? turnModel = string.IsNullOrWhiteSpace(effectiveModel) ? null : new ModelId(effectiveModel);
            var contextualPrompt = InoModel.AttachActiveContext(pending.Text, state);
            await conversation.Say(contextualPrompt, turnModel);
            if (!string.Equals(host.GetState().SelectedModel, displayModel, StringComparison.Ordinal))
            {
                await host.ShowState(host.GetState() with { SelectedModel = displayModel });
            }
            await host.RefreshConversation(false);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(InoModel.BlockedPrefix, StringComparison.Ordinal))
        {
            await host.ShowState(InoModel.StartFailed(host.GetState(), new InoError("paused", error.Message[InoModel.BlockedPrefix.Length..])));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (host.Logger is { } log) { InoLog.LogTurnStartFailed(log, error, conversationKey); }
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
            : !string.IsNullOrWhiteSpace(request.Value) ? request.Value.Trim() : preferred ?? KnownModels.Defaults.Device[ModelRole.Chat].Value;
        var modelId = KnownModels.Find(model)?.Id ?? new ModelId(model);
        await SetChatRoleAsync(host, modelId);
        await ReseatConversationAsync(host, modelId.Value, state.ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), modelId.Value, $"Switched model to {modelId.Value}"));
        return retry;
    }

    public static async Task ChooseModelAsync(
        InoSetupHost host,
        string model,
        bool makeDefault,
        IReadOnlyList<ModelAvailability>? availability = null,
        DeviceView? device = null,
        string? statusOverride = null)
    {
        var trimmed = model.Trim();
        var card = KnownModels.Find(trimmed);
        var modelId = card?.Id ?? new ModelId(trimmed);
        var label = card?.Name ?? modelId.Value;

        if (await TryInstallDeviceModelAsync(host, modelId, card, label, makeDefault, availability, device))
        {
            return;
        }

        var state = host.GetState();
        if (makeDefault)
        {
            await SetChatRoleAsync(host, modelId);
        }

        await ReseatConversationAsync(host, modelId.Value, state.ConversationId);
        if (Threads.IsLocalId(state.ConversationId))
        {
            await host.TryOptional("session-model", () => host.GetSessions().SetModel(state.ConversationId, modelId.Value));
        }
        var status = statusOverride ?? (makeDefault ? $"Default Chat model set to {label}" : $"Switched conversation to {label}");
        await host.ShowState(InoModel.ModelSelected(host.GetState(), modelId.Value, status));
    }

    private static async Task<bool> TryInstallDeviceModelAsync(
        InoSetupHost host,
        ModelId modelId,
        ModelCard? card,
        string label,
        bool makeDefault,
        IReadOnlyList<ModelAvailability>? availability,
        DeviceView? cachedDevice)
    {
        var deviceNeuron = host.GetDevice();
        if (deviceNeuron is null || card is { Where: not Where.Device })
        {
            return false;
        }

        var deviceView = cachedDevice ?? await ReadDeviceAsync(host);
        var deviceModel = deviceView?.Models.FirstOrDefault(m => string.Equals(m.Card.Id.Value, modelId.Value, StringComparison.OrdinalIgnoreCase));
        if (deviceModel is { State: InstallState.Ready or InstallState.Loaded })
        {
            return false;
        }

        if (deviceModel is null && availability?.FirstOrDefault(a => string.Equals(a.Model.Value, modelId.Value, StringComparison.OrdinalIgnoreCase)) is { Ready: true })
        {
            return false;
        }

        if (deviceModel is null && card is null)
        {
            return false;
        }

        if (deviceModel is { Fit: Fit.WontFit })
        {
            await host.ShowState(host.GetState() with { Status = $"{label} is too big for this device." });
            return true;
        }

        if (makeDefault)
        {
            await deviceNeuron.Use(ModelRole.Chat, modelId);
        }
        else
        {
            await deviceNeuron.Install(modelId);
        }

        await host.ShowState(host.GetState() with { Error = null, Status = $"Installing {label}…" });
        return true;
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
        await ReseatConversationAsync(host, defaultModel.Value, host.GetState().ConversationId);
        var retry = host.GetState().Error?.Code is "no-provider" or "provider-rejected-key";
        await host.ShowState(InoModel.ModelSelected(host.GetState(), defaultModel.Value, $"Saved {provider} credential · Ready on {defaultModel.Value}"));
        return retry;
    }

    public static async Task SyncSettingsAsync(InoSetupHost host, UserSettingsChanged changed, InoRuntimeSnapshot? snapshot = null)
    {
        var settings = await host.GetSettings().Read();
        var (_, effectiveModel, displayModel) = await ResolveForSettingsAsync(host, settings, snapshot);
        await EnsureProfileContextAsync(host);
        var conversationId = host.GetState().ConversationId;
        await EnsureSeatedAsync(host, conversationId, settings, effectiveModel);
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
            case "new" or "clear":
                await startConversation();
                return;

            case "compact":
                var conversationKey = InoModel.ConversationKeyOf(state.ConversationId);
                try
                {
                    var conversation = host.GetConversation(conversationKey);
                    await EnsureSeatedAsync(host, state.ConversationId, settings, state.SelectedModel);
                    await host.TryOptional("conversation-synapses", () => host.FollowConversation(conversationKey));
                    await conversation.Say(rawCommand.Trim());
                    await host.RefreshConversation(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    host.LogOptional(error, "conversation-compact");
                    await host.ShowState(state with { Status = "Could not compact conversation." });
                }
                return;

            case "pin":
                await host.ShowState(InoModel.DecideSlashPin(state));
                return;

            case "rename":
                var renamed = InoModel.DecideSlashRename(state, arg);
                if (Threads.IsLocalId(state.ConversationId))
                {
                    await host.TryOptional("session-rename", () => host.GetSessions().Rename(state.ConversationId, renamed.Title));
                }
                await host.ShowState(renamed);
                return;

            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? KnownModels.Defaults.Device[ModelRole.Chat].Value : arg.Trim();
                await ChooseModelAsync(host, nextModel, makeDefault: true, statusOverride: $"Switched model to {nextModel}");
                return;

            default:
                await host.ShowState(InoModel.DecideSlashHelp(state, cmd));
                return;
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

    public static Task<InoCatalogSnapshot?> ReadCatalogSnapshotAsync(InoSetupHost host)
        => host.ReadCatalogOptional("model-catalog", null, async () =>
        {
            var catalog = host.GetCatalog();
            var cards = await catalog.List(Abilities.Text);
            var availability = await catalog.Availability();
            ModelId? picked = null;
            try
            {
                var chosen = await catalog.Pick(ModelRole.Chat);
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
        UserSettingsState settings,
        InoRuntimeSnapshot? snapshot = null)
    {
        var device = snapshot is not null ? snapshot.Device : await ReadDeviceAsync(host);
        var (effective, display) = await ResolveEffectiveModelAsync(host, settings, device, snapshot);
        return (device, effective, display);
    }

    public static async Task<(string? EffectiveModel, string DisplayModel)> ResolveEffectiveModelAsync(
        InoSetupHost host,
        UserSettingsState settings,
        DeviceView? device,
        InoRuntimeSnapshot? runtimeSnapshot = null)
    {
        var personModel = settings.RoleOf(ModelRole.Chat)?.Value;
        if (!string.IsNullOrWhiteSpace(personModel)) { return (personModel, personModel); }

        var preferredLocal = PreferredLocalModel(device);
        var snapshot = runtimeSnapshot is not null ? runtimeSnapshot.Catalog : await ReadCatalogSnapshotAsync(host);
        if (snapshot is not null)
        {
            if (snapshot.PickedChat is { Value.Length: > 0 } picked)
            {
                return (picked.Value, picked.Value);
            }
            var fallbackDisplay = preferredLocal is { Length: > 0 } p ? p : KnownModels.Defaults.Device[ModelRole.Chat].Value;
            return (null, fallbackDisplay);
        }

        var fallback = preferredLocal is { Length: > 0 } detected ? detected : KnownModels.Defaults.Device[ModelRole.Chat].Value;
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

    public static Task EnsureSeatedAsync(
        InoSetupHost host,
        string conversationId,
        UserSettingsState settings,
        string? effectiveModel)
    {
        var effectiveSettings = string.IsNullOrWhiteSpace(effectiveModel)
            ? settings
            : settings with { DefaultModel = effectiveModel.Trim() };
        var definition = InoAgentDefinition.For(effectiveSettings);
        return host.TryOptional("conversation-seat", () => host.GetConversation(InoModel.ConversationKeyOf(conversationId)).Seat("ino", definition));
    }

    private static async Task SetChatRoleAsync(InoSetupHost host, ModelId model)
    {
        var persisted = false;
        await host.TryOptional("model-catalog-set-role", async () =>
        {
            await host.GetCatalog().SetRole(ModelRole.Chat, model);
            persisted = true;
        });
        if (!persisted)
        {
            await host.TryOptional("settings-set-role", () => host.GetSettings().SetRole(ModelRole.Chat, model));
        }
    }

    private static async Task ReseatConversationAsync(InoSetupHost host, string model, string conversationId)
    {
        var settings = await host.ReadSettingsOptional("settings-read", null, async () => await host.GetSettings().Read());
        await EnsureProfileContextAsync(host);
        if (settings is not null)
        {
            await EnsureSeatedAsync(host, conversationId, settings, model);
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

    public static AppBundle BuildBundle(Type compiledType) => AppBundle.FromScreen<InoApp>(
        "ino",
        "3.6.0",
        description: "Talk to your brain, use its capabilities, query databases, and create apps.",
        operations:
        [
            new("open", "Show Ino."),
            new("send", "Send a message to Ino's conversation."),
            new("retry", "Regenerate the last reply."),
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
            new("chats", "Show or hide the recent conversations list."),
            new("toggle-devui", "Toggle DevUI flag."),
            new("make", "Apply a sentence to the focused app, or Scratch."),
            new("link", "Post a pasted workspace link: its app, opaque input and text."),
            new("open-drawer", "Open an artifact, micro-app, or code orchestration run in the inline right-hand drawer."),
            new("close-drawer", "Close the inline artifact drawer."),
            new("drawer-tab", "Switch the inline artifact drawer tab between preview, diff, and logs."),
            new("pin-artifact", "Pin the active code orchestration run as a sandboxed app via CrystallizeAsync."),
            new("clear-context", "Clear the active editing context chip above the composer."),
            new("answer-task-ask", "Answer a pending multi-option ITasks Ask prompt from the docked ChoiceCard."),
        ]) with
    {
        CompiledType = compiledType.AssemblyQualifiedName,
    };
}

internal static partial class InoLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Ino optional dependency '{Target}' was not available.")]
    public static partial void LogOptionalUnavailable(ILogger logger, Exception exception, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ino failed to start turn on conversation '{ConversationKey}'.")]
    public static partial void LogTurnStartFailed(ILogger logger, Exception exception, string conversationKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ino database operation '{Operation}' failed.")]
    public static partial void LogDatabaseFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ino failed to save credential for provider '{Provider}'.")]
    public static partial void LogProviderSaveFailed(ILogger logger, Exception exception, string provider);
}
