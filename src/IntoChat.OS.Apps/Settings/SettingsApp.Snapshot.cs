/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget, set-github-triage-policy, install-issue-triage-synapse, ui.command

Declarative macOS Sonoma-style System Settings application for IntoChat OS (`UK-16` / `INT-1364`).
Owns the 13 settings sections, master-detail subpages (`AppPage`, `MemoryPage`, `ConnectionPage`,
`PersonPage`, `LocalServerPage`), modal sheets, search index, navigation backstack, and pure immutable
`SettingsAppState` DDD Aggregate projections (zero transient mutable grain fields!).
*/
using System.Globalization;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Confirmation;
using DigitalBrain.Kernel;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.Form.Signals;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

public sealed partial class SettingsApp
{
    private async Task RefreshFromNeuronsAsync(string? requestedSection = null)
    {
        var computeProjection = await ReadComputeSummaryAsync(State);
        var approvals = await TryReadPendingApprovalsAsync();
        var user = await UserSettings.Read();
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamRoster = await TryReadTeamRosterAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? State.Variables ?? []);
        var modelInfo = await TryReadModelCatalogAsync(user.DefaultModel);
        var mcpServers = await TryReadMcpServersAsync(State.McpServers);
        var (webProvider, searxngUrl) = ResolveWebSearchSettings(user.Variables ?? State.Variables ?? []);
        var displayModel = string.IsNullOrWhiteSpace(user.DefaultModel)
            ? (modelInfo.AutomaticModel ?? KnownModels.Defaults.Device[ModelRole.Chat].Value)
            : user.DefaultModel.Trim();
        var effectiveUser = string.Equals(displayModel, user.DefaultModel, StringComparison.Ordinal)
            ? user
            : user with { DefaultModel = displayModel };
        var targetSection = string.IsNullOrWhiteSpace(requestedSection) ? State.Section : NormalizeSection(requestedSection);

        var projected = computeProjection with
        {
            PendingApprovals = approvals,
            SecretStatuses = secrets,
            Roles = modelInfo.Roles,
            Providers = modelInfo.Providers,
            LocalOllamaEndpoint = modelInfo.LocalEndpoint,
            Device = modelInfo.Device,
            McpServers = mcpServers,
            WebSearchProvider = webProvider,
            WebSearxngUrl = searxngUrl,
        };

        var next = projected.ApplyUserSettings(
            effectiveUser,
            $"Settings ({effectiveUser.DisplayName} · {effectiveUser.Theme} · {effectiveUser.DefaultModel})",
            deployedUnits,
            teamRoster,
            section: targetSection);

        if (string.Equals(next.Section, "connections", StringComparison.Ordinal) && SettingsAppState.IsGitHubSubPage(next.SubPage))
        {
            next = await RefreshGitHubHubProjectionAsync(next);
        }

        await UpdateAsync(next);
    }

    private static readonly AiProvider[] SettingsProviders =
    [
        AiProvider.Ollama,
        AiProvider.FoundryLocal,
        AiProvider.OpenAI,
        AiProvider.Anthropic,
        AiProvider.Google,
        AiProvider.XAI,
        AiProvider.OpenRouter,
    ];

    private async Task SetRoleAsync(ModelRole role, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var trimmed = modelId.Trim();
        var chosen = new ModelId(trimmed);
        var persistedViaCatalog = false;

        if (TryBrainOrNull() is { Value.Length: > 0 })
        {
            try
            {
                if (ModelCatalog is { } catalog)
                {
                    await catalog.SetRole(role, chosen);
                    persistedViaCatalog = true;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IModelCatalog is optional when SettingsApp runs in standalone tests.");
            }
        }

        if (!persistedViaCatalog)
        {
            try
            {
                await UserSettings.SetRole(role, chosen);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Skipping role persistence in minimal test host.");
            }
        }

        var nextDefaultModel = role == ModelRole.Chat ? trimmed : State.DefaultModel;
        var modelInfo = await TryReadModelCatalogAsync(nextDefaultModel);
        var baselineRoles = modelInfo.Roles.Length > 0
            ? modelInfo.Roles
            : State.Roles.Length > 0
                ? State.Roles
                : SettingsViewRenderer.DefaultRoles(State);
        var updatedRoles = baselineRoles
            .Select(row => row.Role == role ? row with { Selected = trimmed } : row)
            .ToArray();

        await UpdateAsync(State with
        {
            DefaultModel = nextDefaultModel,
            Roles = updatedRoles,
            Providers = modelInfo.Providers.Length > 0 ? modelInfo.Providers : State.Providers,
            LocalOllamaEndpoint = modelInfo.LocalEndpoint,
            Device = modelInfo.Device ?? State.Device,
            Status = $"Updated {role} model to {KnownModels.Find(trimmed)?.Name ?? trimmed}.",
        });
    }

    private async Task InstallModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var trimmed = model.Trim();
        var modelId = KnownModels.Find(trimmed)?.Id ?? new ModelId(trimmed);
        if (TryBrainOrNull() is not { Value.Length: > 0 } brain || ThisMachine is not { Value.Length: > 0 } machine)
        {
            await UpdateAsync(State with { Status = "Device runtime is unavailable in this host." });
            return;
        }

        try
        {
            var device = Devices[new NeuronId(brain, "devices.device", $"devices/{machine.Value}")];
            await device.Install(modelId);
            var snapshot = await device.Read();
            var modelInfo = await TryReadModelCatalogAsync(State.DefaultModel);
            await UpdateAsync(State with
            {
                Roles = modelInfo.Roles.Length > 0 ? modelInfo.Roles : State.Roles,
                Providers = modelInfo.Providers.Length > 0 ? modelInfo.Providers : State.Providers,
                LocalOllamaEndpoint = modelInfo.LocalEndpoint,
                Device = snapshot,
                Status = $"Queued install for {modelId.Value}.",
            });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            await UpdateAsync(State with { Status = error.Message });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IDevice.Install failed while installing {ModelId} from Settings.", modelId.Value);
            await UpdateAsync(State with { Status = $"Could not install {modelId.Value}." });
        }
    }

    private async Task<(SettingsRoleRow[] Roles, SettingsProviderRow[] Providers, string? AutomaticModel, string LocalEndpoint, DeviceView? Device)> TryReadModelCatalogAsync(string? personModel)
    {
        BrainId brain = default;
        try
        {
            brain = Id.Brain;
        }
        catch (InvalidOperationException error)
        {
            Log.LogDebug(error, "Settings brain id is unavailable during standalone test activation.");
            return (State.Roles, State.Providers, null, State.LocalOllamaEndpoint, State.Device);
        }

        if (string.IsNullOrWhiteSpace(brain.Value))
        {
            return (State.Roles, State.Providers, null, State.LocalOllamaEndpoint, State.Device);
        }

        DeviceView? deviceView = null;
        if (ThisMachine is { Value.Length: > 0 } machine)
        {
            try
            {
                deviceView = await Devices[new NeuronId(brain, "devices.device", $"devices/{machine.Value}")].Read();
            }
            catch (Exception deviceError) when (deviceError is not OperationCanceledException)
            {
                Log.LogDebug(deviceError, "IDevice.Read failed while refreshing Settings AI section.");
            }
        }

        var ollamaEndpoint = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.Ollama)?.Endpoint?.ToString();
        var endpoint = !string.IsNullOrWhiteSpace(ollamaEndpoint)
            ? ollamaEndpoint
            : string.IsNullOrWhiteSpace(State.LocalOllamaEndpoint)
                ? IntoChatOSNames.LocalOllamaEndpoint
                : State.LocalOllamaEndpoint;

        try
        {
            if (ModelCatalog is not { } catalog)
            {
                var fallbackProviders = deviceView is not null
                    ? BuildProviderRows([], new Dictionary<string, ModelAvailability>(StringComparer.OrdinalIgnoreCase), deviceView, endpoint)
                    : State.Providers;
                return (State.Roles, fallbackProviders, null, endpoint, deviceView);
            }
            var cards = await catalog.List();
            var availability = (await catalog.Availability())
                .ToDictionary(static a => a.Model.Value, StringComparer.OrdinalIgnoreCase);
            var roleMap = await catalog.Roles();

            var roles = BuildRoleRows(cards, availability, roleMap, personModel);
            var providers = BuildProviderRows(cards, availability, deviceView, endpoint);

            string? autoModel = null;
            try
            {
                var picked = await catalog.Pick(ModelRole.Chat, string.IsNullOrWhiteSpace(personModel) ? null : new ModelId(personModel.Trim()));
                if (!string.IsNullOrWhiteSpace(picked.Value))
                {
                    autoModel = picked.Value;
                }
            }
            catch (Exception pickError) when (pickError is not OperationCanceledException)
            {
                Log.LogDebug(pickError, "IModelCatalog.Pick did not resolve an automatic chat model.");
            }

            return (roles, providers, autoModel, endpoint, deviceView);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IModelCatalog is optional when SettingsApp runs in standalone tests.");
            var providers = deviceView is not null
                ? BuildProviderRows([], new Dictionary<string, ModelAvailability>(StringComparer.OrdinalIgnoreCase), deviceView, endpoint)
                : State.Providers;
            return (State.Roles, providers, null, endpoint, deviceView);
        }
    }

    private static SettingsRoleRow[] BuildRoleRows(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyDictionary<string, ModelAvailability> availability,
        RoleMap roleMap,
        string? personModel)
    {
        var allRoles = Enum.GetValues<ModelRole>();
        var rows = new SettingsRoleRow[allRoles.Length];
        for (var i = 0; i < allRoles.Length; i++)
        {
            var role = allRoles[i];
            var needed = RoleNeeds.Of(role);
            var deduped = new Dictionary<string, SettingsModelChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if ((card.Can & needed) != needed)
                {
                    continue;
                }

                var id = card.Id.Value;
                var available = availability.TryGetValue(id, out var avail) && avail.Ready;
                var reason = available ? null : avail?.Reason;
                var choice = new SettingsModelChoice(id, card.Name, available, reason);
                if (!deduped.TryGetValue(id, out var existing) || (!existing.Available && choice.Available))
                {
                    deduped[id] = choice;
                }
            }

            string? selected = null;
            var hasDevice = roleMap.Device.TryGetValue(role, out var deviceId);
            var hasCloud = roleMap.Cloud.TryGetValue(role, out var cloudId);
            if (hasDevice && availability.TryGetValue(deviceId.Value, out var deviceAvail) && deviceAvail.Ready)
            {
                selected = deviceId.Value;
            }
            else if (hasCloud && availability.TryGetValue(cloudId.Value, out var cloudAvail) && cloudAvail.Ready)
            {
                selected = cloudId.Value;
            }
            else if (hasDevice)
            {
                selected = deviceId.Value;
            }
            else if (hasCloud)
            {
                selected = cloudId.Value;
            }
            else if (role == ModelRole.Chat && !string.IsNullOrWhiteSpace(personModel))
            {
                selected = personModel.Trim();
            }

            rows[i] = new SettingsRoleRow(role, selected, [.. deduped.Values]);
        }

        return rows;
    }

    private static SettingsProviderRow[] BuildProviderRows(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyDictionary<string, ModelAvailability> availability,
        DeviceView? deviceView,
        string localEndpoint)
    {
        var rows = new List<SettingsProviderRow>(SettingsProviders.Length + (deviceView?.Runtimes.Length ?? 0));
        foreach (var provider in SettingsProviders)
        {
            var visual = VisualCatalog.ForProvider(provider.ToString());
            var label = provider switch
            {
                AiProvider.Ollama => "Ollama",
                AiProvider.FoundryLocal => "Foundry Local",
                _ => visual.Label,
            };

            string status;
            if (provider == AiProvider.FoundryLocal)
            {
                var foundry = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.FoundryLocal);
                var installedCount = deviceView?.Models.Count(static m =>
                    m.Runtime == RuntimeId.FoundryLocal && m.State is InstallState.Ready or InstallState.Loaded) ?? 0;
                status = FormatRuntimeStatus(foundry, installedCount, fallbackUnavailable: "Not available");
            }
            else if (provider == AiProvider.Ollama)
            {
                var ollama = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.Ollama);
                var installedOnDevice = deviceView?.Models.Count(static m =>
                    m.Runtime == RuntimeId.Ollama && m.State is InstallState.Ready or InstallState.Loaded) ?? 0;
                var readyLocalCount = cards.Count(c =>
                    c.Provider == AiProvider.Ollama
                    && availability.TryGetValue(c.Id.Value, out var a)
                    && a.Ready);
                status = ollama is not null
                    ? FormatRuntimeStatus(ollama, installedOnDevice, fallbackUnavailable: $"Not detected · {ollama.Endpoint?.ToString() ?? localEndpoint}")
                    : readyLocalCount > 0
                        ? $"Ready · {localEndpoint} · {readyLocalCount} installed"
                        : $"Not detected · {localEndpoint}";
            }
            else
            {
                var anyReady = cards.Any(c =>
                    c.Provider == provider
                    && availability.TryGetValue(c.Id.Value, out var a)
                    && a.Ready);
                status = anyReady ? "Ready" : "Add a key in API keys";
            }

            rows.Add(new SettingsProviderRow(provider, label, visual.IconKey, status));

            if (provider == AiProvider.FoundryLocal && deviceView is not null)
            {
                var compatVisual = VisualCatalog.ForProvider(nameof(AiProvider.OpenAICompatible));
                foreach (var runtime in deviceView.Runtimes)
                {
                    if (runtime.Id == RuntimeId.Ollama || runtime.Id == RuntimeId.FoundryLocal)
                    {
                        continue;
                    }
                    var installedCount = deviceView.Models.Count(m =>
                        m.Runtime == runtime.Id && m.State is InstallState.Ready or InstallState.Loaded);
                    rows.Add(new SettingsProviderRow(
                        AiProvider.OpenAICompatible,
                        FormatAttachedRuntimeLabel(runtime.Id),
                        compatVisual.IconKey,
                        FormatRuntimeStatus(runtime, installedCount, fallbackUnavailable: FormatUnavailableRuntime(runtime))));
                }
            }
        }

        return [.. rows];
    }

    internal static string FormatAttachedRuntimeLabel(RuntimeId id)
    {
        const string prefix = "openai-compat:";
        return id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Value.Length > prefix.Length
            ? id.Value[prefix.Length..]
            : id.Value;
    }

    internal static string FormatRuntimeStatus(RuntimeStatus? runtime, int installedCount, string fallbackUnavailable)
    {
        if (runtime is not { Reachable: true })
        {
            return fallbackUnavailable;
        }

        var parts = new List<string>(4) { "Ready" };
        if (!string.IsNullOrWhiteSpace(runtime.Version))
        {
            parts.Add($"v{runtime.Version.TrimStart('v', 'V')}");
        }
        if (runtime.Endpoint is not null)
        {
            parts.Add(runtime.Endpoint.ToString().TrimEnd('/'));
        }
        parts.Add($"{installedCount} installed");
        return string.Join(" · ", parts);
    }

    internal static string FormatUnavailableRuntime(RuntimeStatus runtime)
        => runtime.Endpoint is not null
            ? $"Unreachable · {runtime.Endpoint.ToString().TrimEnd('/')}"
            : "Unreachable";

    private async Task<IReadOnlyDictionary<string, bool>> InspectSecretStatusesAsync(IReadOnlyList<UserVariableEntry> variables)
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var vault = SecretVault ?? Vault;
        string[] secretKeys = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY", "GOOGLE_API_KEY", "BRAVE_API_KEY", "TAVILY_API_KEY"];
        foreach (var key in secretKeys)
        {
            if (vault != null)
            {
                try
                {
                    var statusText = await vault.GetSecretStatusAsync(key);
                    dict[key] = string.Equals(statusText, "Configured", StringComparison.OrdinalIgnoreCase)
                        || await vault.HasSecretAsync(key);
                    continue;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "Failed probing secret vault for {Key}; falling back to local variable check.", key);
                }
            }

            dict[key] = variables.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Value));
        }

        return dict;
    }

    private Task ApplyUserSettingsAsync(
        UserSettingsState user,
        string status,
        IReadOnlyList<string>? deployedUnits = null,
        Friend[]? teamRoster = null,
        string? section = null,
        string? archiveJson = null,
        SettingsAppState? baseline = null)
        => UpdateAsync((baseline ?? State).ApplyUserSettings(user, status, deployedUnits, teamRoster, section, archiveJson));

    private async Task<IReadOnlyList<string>> TryReadDeployedUnitsAsync()
    {
        try
        {
            var deployed = await SynapseStudio.List();
            return [.. deployed.Select(d => d.SourceKey).Where(k => !string.IsNullOrWhiteSpace(k)).OrderBy(k => k, StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "SynapseStudio is optional when SettingsApp runs in standalone tests.");
            return State.DeployedUnits ?? [];
        }
    }

    private async Task<Friend[]?> TryReadTeamRosterAsync()
    {
        try
        {
            return Friends is { } friends ? await friends.List() : State.TeamRoster;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "FriendsNeuron is optional when SettingsApp runs in standalone tests.");
            return State.TeamRoster;
        }
    }

    private async Task<IReadOnlyList<Approval>> TryReadPendingApprovalsAsync()
    {
        try
        {
            return Approvals is { } approvals ? [.. await approvals.Pending()] : State.PendingApprovals;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "ApprovalsNeuron is optional when SettingsApp runs in standalone tests.");
            return State.PendingApprovals;
        }
    }

    private async Task<SettingsAppState> ReadComputeSummaryAsync(SettingsAppState baseline)
    {
        try
        {
            if (Compute is not { } computeSlot)
            {
                return baseline;
            }
            var compute = await computeSlot.Read();
            var budgetMicros = compute.MonthlyBudgetMicros > 0
                ? compute.MonthlyBudgetMicros
                : ComputeState.DefaultMonthlyBudgetMicros;
            return baseline with
            {
                ComputeSpendUsd = SettingsAppState.FormatUsd(compute.SpentMicros),
                ComputeBudgetUsd = SettingsAppState.FormatUsd(budgetMicros),
                MonthlyBudgetMicros = budgetMicros,
                ComputeBudgetExceeded = compute.BudgetExceeded,
                ComputeRowsSummary = $"{compute.Rows.Length} model(s) metered",
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
            return baseline;
        }
    }

    [On]
    public async Task Navigate(SettingsNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.Navigate(request.Section, request.SubPage, request.Sheet, request.SearchQuery);
        if (string.Equals(next.Section, "models", StringComparison.Ordinal))
        {
            var modelInfo = await TryReadModelCatalogAsync(next.DefaultModel);
            next = next with
            {
                Roles = modelInfo.Roles,
                Providers = modelInfo.Providers,
                LocalOllamaEndpoint = modelInfo.LocalEndpoint,
                Device = modelInfo.Device,
            };

            if (string.Equals(next.SubPage, "web-tools", StringComparison.OrdinalIgnoreCase))
            {
                next = await RefreshWebToolsProjectionAsync(next);
            }
        }
        else if (string.Equals(next.Section, "keys", StringComparison.Ordinal))
        {
            var secrets = await InspectSecretStatusesAsync(next.Variables ?? []);
            next = next with { SecretStatuses = secrets };
        }
        else if (string.Equals(next.Section, "connections", StringComparison.Ordinal) && SettingsAppState.IsGitHubSubPage(next.SubPage))
        {
            next = next with { GitHubConnectionSlug = SettingsAppState.ResolveGitHubSlug(next.SubPage, next.GitHubConnectionSlug) };
            await EnsureGitHubPatFormConfiguredAsync();
            next = await RefreshGitHubHubProjectionAsync(next);
        }

        await UpdateAsync(next);
    }

    [On]
    public Task GoBack(SettingsGoBack request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateAsync(State.GoBack());
    }

    [On]
    public Task RefreshConnections(SettingsRefreshConnections request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConnectionKey is not null && string.IsNullOrWhiteSpace(request.ConnectionKey) && string.IsNullOrWhiteSpace(request.FriendApp))
        {
            return ApplySettingsDecisionAsync(State.RegisterConnection(request.ConnectionKey, request.FriendApp));
        }

        return UpdateAsync(State.RefreshConnectionsProjection(request.ConnectionKey, request.FriendApp));
    }

    [On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (new[]
            {
                request.DisplayName, request.Theme, request.DefaultModel, request.StylePreset, request.BarLayout,
                request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat,
            }.All(static v => v is null))
        {
            await UpdateAsync(State with { Title = "Settings", Status = "Nothing to save." });
            return;
        }

        var validation = State.ApplyPreferences(
            request.DisplayName,
            request.Theme,
            request.DefaultModel,
            request.StylePreset,
            request.BarLayout,
            request.ThemeMode,
            request.DesktopTone);
        if (validation.IsRefused)
        {
            await ApplySettingsDecisionAsync(validation);
            return;
        }

        if (new[]
            {
                request.DisplayName, request.Theme, request.DefaultModel, request.StylePreset, request.BarLayout,
                request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat,
            }.All(string.IsNullOrWhiteSpace))
        {
            await UpdateAsync(State with { Title = "Settings", Status = "Nothing to save." });
            return;
        }

        var store = ResolvePreferenceStore();
        var updated = await store.SaveAsync(request, State);
        await ApplyUserSettingsAsync(updated, $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel}, {updated.StylePreset}, {updated.BarLayout})");
    }

    [On]
    public async Task SetVariable(SettingsSetVariable request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var check = State.UpsertVariable(request.Key, request.Value);
        if (check.IsRefused)
        {
            await ApplySettingsDecisionAsync(check);
            return;
        }

        var key = request.Key!.Trim();
        var val = request.Value ?? "";
        var updated = await UserSettings.SetVariable(key, val);
        var variables = updated.Variables ?? [];
        var secrets = await InspectSecretStatusesAsync(variables);
        await UpdateAsync((State with { SecretStatuses = secrets }).ApplyStoredVariables(key, variables));
    }

    [On]
    public async Task SetBudget(SettingsSetBudget request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cap = request.MonthlyBudgetMicros;
        var decision = State.ApplyBudgetCap(cap);
        if (decision.IsRefused)
        {
            await ApplySettingsDecisionAsync(decision);
            return;
        }

        var next = decision.Next;
        try
        {
            if (Compute is { } compute)
            {
                await compute.SetBudget(cap);
                next = await ReadComputeSummaryAsync(next);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
        }

        await UpdateAsync(next with
        {
            Section = "billing",
            Status = $"Updated monthly compute cap to {next.ComputeBudgetUsd}.",
        });
    }

    [On]
    public async Task ExportBrain(SettingsExportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brainId = TryCurrentBrainId();
        var snapshot = await PortableBrainArchive.ExportAsync(brainId, UserSettings, Profile, SynapseStudio);
        await UpdateAsync(State.RecordExportedArchive(PortableBrainArchive.ToJson(snapshot), snapshot.Units.Count));
    }

    [On]
    public async Task McpAdd(SettingsMcpAdd request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check(
                "mcp-add",
                SettingsAppState.ValidateMcpServerNameRule(request.Name),
                SettingsAppState.ValidateMcpServerUrlRule(request.Url)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var name = request.Name!.Trim().ToLowerInvariant();
        var url = request.Url!.Trim();
        var title = string.IsNullOrWhiteSpace(request.Title) ? name : request.Title.Trim();

        McpServerRow row;
        string status;
        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var serverId = new NeuronId(brain, "mcp.server", "mcp/" + name);
            try
            {
                var connected = await McpServers[serverId].Connect(new McpServerSpec(title, url));
                row = new McpServerRow(
                    Name: name,
                    Title: string.IsNullOrWhiteSpace(connected.Title) ? title : connected.Title,
                    Url: string.IsNullOrWhiteSpace(connected.Url) ? url : connected.Url,
                    Connected: connected.Connected,
                    ToolCount: connected.Tools?.Length ?? 0,
                    Error: connected.Error);
                status = connected.Connected
                    ? $"Connected MCP server '{row.Title}' ({row.ToolCount} tool(s))."
                    : $"MCP server '{row.Title}' is disconnected.";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogWarning(error, "Failed to connect MCP server '{ServerName}' at {Url}.", name, url);
                row = new McpServerRow(name, title, url, Connected: false, ToolCount: 0, Error: error.Message);
                status = $"Failed to connect MCP server '{name}': {error.Message}";
            }
        }
        else
        {
            row = new McpServerRow(name, title, url, Connected: false, ToolCount: 0, Error: "Brain context is unavailable.");
            status = $"Brain context is unavailable for MCP server '{name}'.";
        }

        await UpdateAsync(State with
        {
            McpServers = UpsertMcpRow(State.McpServers, row),
            Status = status,
        });
    }

    [On]
    public async Task McpRemove(SettingsMcpRemove request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("mcp-remove", SettingsAppState.ValidateMcpServerNameRule(request.Name)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var name = request.Name!.Trim().ToLowerInvariant();
        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var serverId = new NeuronId(brain, "mcp.server", "mcp/" + name);
            try
            {
                await McpServers[serverId].Disconnect();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IMcpServer.Disconnect failed for '{ServerName}' in minimal host.", name);
            }
        }

        var updatedRows = (State.McpServers ?? [])
            .Select(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)
                ? row with { Connected = false }
                : row)
            .OrderBy(static r => r.Name, StringComparer.Ordinal)
            .ToArray();

        await UpdateAsync(State with
        {
            McpServers = updatedRows,
            Status = $"Disconnected MCP server '{name}'.",
        });
    }

    private async Task SetWebSearchProviderAsync(string? rawProvider)
    {
        var normalized = SettingsControls.TryNormalizeSearchProvider(rawProvider);
        if (normalized is null)
        {
            return;
        }

        IReadOnlyList<UserVariableEntry> variables = State.Variables ?? [];
        try
        {
            var updated = await UserSettings.SetVariable(SettingsControls.WebSearchProviderVariable, normalized);
            variables = updated.Variables ?? [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Skipping web.search.provider persistence in minimal test host.");
            variables = UpsertVariableEntry(variables, SettingsControls.WebSearchProviderVariable, normalized);
        }

        await UpdateAsync(State with
        {
            Variables = [.. variables.Where(static v => !string.IsNullOrWhiteSpace(v.Key))],
            WebSearchProvider = normalized,
            Status = $"Web search provider set to {normalized}.",
        });
    }

    private async Task SetSearxngUrlAsync(string rawUrl)
    {
        if (Decision.Check("set-searxng-url", SettingsAppState.ValidateSearxngUrlRule(rawUrl)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var trimmed = rawUrl.Trim();
        IReadOnlyList<UserVariableEntry> variables = State.Variables ?? [];
        try
        {
            var updated = await UserSettings.SetVariable(SettingsControls.WebSearxngUrlVariable, trimmed);
            variables = updated.Variables ?? [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Skipping web.search.searxng-url persistence in minimal test host.");
            variables = UpsertVariableEntry(variables, SettingsControls.WebSearxngUrlVariable, trimmed);
        }

        await UpdateAsync(State with
        {
            Variables = [.. variables.Where(static v => !string.IsNullOrWhiteSpace(v.Key))],
            WebSearxngUrl = trimmed,
            Status = string.IsNullOrEmpty(trimmed) ? "Cleared SearXNG URL override." : $"Saved SearXNG URL ({trimmed}).",
        });
    }

    private async Task<SettingsAppState> RefreshWebToolsProjectionAsync(SettingsAppState baseline)
    {
        IReadOnlyList<UserVariableEntry> variables = baseline.Variables ?? [];
        try
        {
            var user = await UserSettings.Read();
            if (user.Variables is not null)
            {
                variables = user.Variables;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IUserSettings is optional when refreshing Web & tools in standalone tests.");
        }

        var secrets = await InspectSecretStatusesAsync(variables);
        var servers = await TryReadMcpServersAsync(baseline.McpServers);
        var (webProvider, searxngUrl) = ResolveWebSearchSettings(variables);

        return baseline with
        {
            Variables = [.. variables.Where(static v => !string.IsNullOrWhiteSpace(v.Key))],
            SecretStatuses = secrets,
            McpServers = servers,
            WebSearchProvider = webProvider,
            WebSearxngUrl = searxngUrl,
        };
    }

    private (string? Provider, string? SearxngUrl) ResolveWebSearchSettings(IReadOnlyList<UserVariableEntry> variables)
    {
        var configured = WebOptions?.Value;
        var provider = configured?.Provider.ToString() ?? State.WebSearchProvider;
        var searxngUrl = configured?.SearxngUrl?.ToString() ?? State.WebSearxngUrl;

        foreach (var entry in variables)
        {
            if (string.Equals(entry.Key, SettingsControls.WebSearchProviderVariable, StringComparison.Ordinal)
                && SettingsControls.TryNormalizeSearchProvider(entry.Value) is { } overrideProvider)
            {
                provider = overrideProvider;
            }
            else if (string.Equals(entry.Key, SettingsControls.WebSearxngUrlVariable, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                searxngUrl = entry.Value.Trim();
            }
        }

        return (provider, searxngUrl);
    }

    private async Task<McpServerRow[]> TryReadMcpServersAsync(McpServerRow[]? existing)
    {
        var existingByName = (existing ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (TryBrainOrNull() is not { Value.Length: > 0 } brain)
        {
            return [.. existingByName.Values.OrderBy(static r => r.Name, StringComparer.Ordinal)];
        }

        var candidates = new Dictionary<string, NeuronId>(StringComparer.OrdinalIgnoreCase);
        var observer = Observer;
        if (observer is not null)
        {
            try
            {
                await foreach (var instance in observer.Instances("mcp.server"))
                {
                    if (TryExtractMcpServer(brain, instance.Key, out var id, out var name))
                    {
                        candidates[name] = id;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IBrainObserver.Instances(\"mcp.server\") failed while refreshing Settings Web & tools.");
            }
        }

        if (McpOptions?.Value?.Servers is { Count: > 0 } configuredServers)
        {
            foreach (var name in configuredServers.Keys)
            {
                if (!string.IsNullOrWhiteSpace(name) && !candidates.ContainsKey(name))
                {
                    candidates[name] = new NeuronId(brain, "mcp.server", "mcp/" + name.Trim().ToLowerInvariant());
                }
            }
        }

        foreach (var name in existingByName.Keys)
        {
            if (!candidates.ContainsKey(name))
            {
                candidates[name] = new NeuronId(brain, "mcp.server", "mcp/" + name.Trim().ToLowerInvariant());
            }
        }

        var rows = new Dictionary<string, McpServerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, neuronId) in candidates)
        {
            try
            {
                var snapshot = await McpServers[neuronId].Read();
                var hasData = !string.IsNullOrWhiteSpace(snapshot.Url)
                    || !string.IsNullOrWhiteSpace(snapshot.Title)
                    || snapshot.Connected
                    || !string.IsNullOrWhiteSpace(snapshot.Error)
                    || snapshot.RefreshedAt is not null;
                if (hasData)
                {
                    rows[name] = new McpServerRow(
                        Name: name,
                        Title: string.IsNullOrWhiteSpace(snapshot.Title) ? name : snapshot.Title,
                        Url: snapshot.Url,
                        Connected: snapshot.Connected,
                        ToolCount: snapshot.Tools?.Length ?? 0,
                        Error: snapshot.Error);
                }
                else if (existingByName.TryGetValue(name, out var preserved))
                {
                    rows[name] = preserved;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IMcpServer.Read is optional for '{ServerName}' in standalone tests.", name);
                if (existingByName.TryGetValue(name, out var preserved))
                {
                    rows[name] = preserved;
                }
            }
        }

        return [.. rows.Values.OrderBy(static r => r.Name, StringComparer.Ordinal)];
    }

    private static bool TryExtractMcpServer(BrainId brain, string instanceKey, out NeuronId neuronId, out string name)
    {
        if (NeuronId.TryParse(instanceKey, out var parsed)
            && string.Equals(parsed.Type, "mcp.server", StringComparison.Ordinal)
            && parsed.Brain == brain)
        {
            neuronId = parsed;
            name = ExtractMcpName(parsed.Key);
            return !string.IsNullOrWhiteSpace(name);
        }

        var prefix = brain.Value + "/";
        if (instanceKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            var localKey = instanceKey[prefix.Length..];
            neuronId = new NeuronId(brain, "mcp.server", localKey);
            name = ExtractMcpName(localKey);
            return !string.IsNullOrWhiteSpace(name);
        }

        neuronId = default;
        name = string.Empty;
        return false;
    }

    private static string ExtractMcpName(string localKey)
    {
        var trimmed = localKey.Trim('/');
        if (trimmed.StartsWith("mcp/", StringComparison.Ordinal))
        {
            return trimmed["mcp/".Length..];
        }

        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static McpServerRow[] UpsertMcpRow(McpServerRow[]? existing, McpServerRow row)
    {
        var byName = (existing ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
        byName[row.Name] = row;
        return [.. byName.Values.OrderBy(static r => r.Name, StringComparer.Ordinal)];
    }

    private static UserVariableEntry[] UpsertVariableEntry(IReadOnlyList<UserVariableEntry> existing, string key, string value)
    {
        var list = existing.ToList();
        var idx = list.FindIndex(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            list[idx] = new UserVariableEntry(key, value);
        }
        else
        {
            list.Add(new UserVariableEntry(key, value));
        }

        return [.. list];
    }

}
