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
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.Form.Signals;
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
        var user = await Get<IUserSettings>("settings").Read();
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamFriends = await TryReadTeamFriendsAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? State.Variables ?? []);
        var modelInfo = await TryReadModelCatalogAsync(user.DefaultModel);
        var displayModel = string.IsNullOrWhiteSpace(user.DefaultModel)
            ? (modelInfo.AutomaticModel ?? KnownModels.Defaults.Device[Role.Chat].Value)
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
        };

        var next = projected.ApplyUserSettings(
            effectiveUser,
            $"Settings ({effectiveUser.DisplayName} · {effectiveUser.Theme} · {effectiveUser.DefaultModel})",
            deployedUnits,
            teamFriends,
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

    private async Task SetRoleAsync(Role role, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var trimmed = modelId.Trim();
        var chosen = new ModelId(trimmed);
        var persistedViaCatalog = false;

        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            try
            {
                await Get<IModelCatalog>(IModelCatalog.Key(brain.Value)).SetRole(role, chosen);
                persistedViaCatalog = true;
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
                await Get<IUserSettings>("settings").SetRole(role, chosen);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Skipping role persistence in minimal test host.");
            }
        }

        var nextDefaultModel = role == Role.Chat ? trimmed : State.DefaultModel;
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
            Status = $"Updated {role} model to {KnownModels.Find(trimmed)?.Name ?? trimmed}.",
        });
    }

    private async Task<(SettingsRoleRow[] Roles, SettingsProviderRow[] Providers, string? AutomaticModel, string LocalEndpoint)> TryReadModelCatalogAsync(string? personModel)
    {
        BrainId brain = default;
        try
        {
            brain = Id.Brain;
        }
        catch (InvalidOperationException error)
        {
            Log.LogDebug(error, "Settings brain id is unavailable during standalone test activation.");
            return (State.Roles, State.Providers, null, State.LocalOllamaEndpoint);
        }

        if (string.IsNullOrWhiteSpace(brain.Value))
        {
            return (State.Roles, State.Providers, null, State.LocalOllamaEndpoint);
        }

        try
        {
            var catalog = Get<IModelCatalog>(IModelCatalog.Key(brain.Value));
            var cards = await catalog.List();
            var availability = (await catalog.Availability())
                .ToDictionary(static a => a.Model.Value, StringComparer.OrdinalIgnoreCase);
            var roleMap = await catalog.Roles();

            DeviceView? deviceView = null;
            if (ThisMachine is { Value.Length: > 0 } machine)
            {
                try
                {
                    deviceView = await Get<IDevice>(new NeuronId(brain, "devices.device", $"devices/{machine.Value}")).Read();
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
            var roles = BuildRoleRows(cards, availability, roleMap, personModel);
            var providers = BuildProviderRows(cards, availability, deviceView, endpoint);

            string? autoModel = null;
            try
            {
                var picked = await catalog.Pick(Role.Chat, string.IsNullOrWhiteSpace(personModel) ? null : new ModelId(personModel.Trim()));
                if (!string.IsNullOrWhiteSpace(picked.Value))
                {
                    autoModel = picked.Value;
                }
            }
            catch (Exception pickError) when (pickError is not OperationCanceledException)
            {
                Log.LogDebug(pickError, "IModelCatalog.Pick did not resolve an automatic chat model.");
            }

            return (roles, providers, autoModel, endpoint);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IModelCatalog is optional when SettingsApp runs in standalone tests.");
            return (State.Roles, State.Providers, null, State.LocalOllamaEndpoint);
        }
    }

    private static SettingsRoleRow[] BuildRoleRows(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyDictionary<string, ModelAvailability> availability,
        RoleMap roleMap,
        string? personModel)
    {
        var allRoles = Enum.GetValues<Role>();
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
            else if (role == Role.Chat && !string.IsNullOrWhiteSpace(personModel))
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
        var rows = new SettingsProviderRow[SettingsProviders.Length];
        for (var i = 0; i < SettingsProviders.Length; i++)
        {
            var provider = SettingsProviders[i];
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
                status = foundry is { Reachable: true }
                    ? $"Ready · {foundry.Version ?? "local"} · {installedCount} installed"
                    : "Not available";
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
                status = ollama is { Reachable: true }
                    ? $"Ready · {ollama.Endpoint?.ToString() ?? localEndpoint} · {installedOnDevice} installed"
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

            rows[i] = new SettingsProviderRow(provider, label, visual.IconKey, status);
        }

        return rows;
    }

    private async Task<IReadOnlyDictionary<string, bool>> InspectSecretStatusesAsync(IReadOnlyList<UserVariableEntry> variables)
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var vault = SecretVault ?? TryGetOptional<ISecretVault>("vault");
        string[] secretKeys = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY", "GOOGLE_API_KEY"];
        foreach (var key in secretKeys)
        {
            if (vault != null)
            {
                try
                {
                    dict[key] = await vault.HasSecretAsync(key);
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
        IReadOnlyList<ProfileFriend>? teamFriends = null,
        string? section = null,
        string? archiveJson = null,
        SettingsAppState? baseline = null)
        => UpdateAsync((baseline ?? State).ApplyUserSettings(user, status, deployedUnits, teamFriends, section, archiveJson));

    private async Task<IReadOnlyList<string>> TryReadDeployedUnitsAsync()
    {
        try
        {
            var deployed = await Get<ISynapseStudio>("ino/synapses").List();
            return [.. deployed.Select(d => d.SourceKey).Where(k => !string.IsNullOrWhiteSpace(k)).OrderBy(k => k, StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "SynapseStudio is optional when SettingsApp runs in standalone tests.");
            return State.DeployedUnits ?? [];
        }
    }

    private async Task<IReadOnlyList<ProfileFriend>> TryReadTeamFriendsAsync()
    {
        try
        {
            return (await Get<IProfile>("profile").Read()).Friends ?? [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "ProfileNeuron is optional when SettingsApp runs in standalone tests.");
            return State.TeamFriends ?? [];
        }
    }

    private async Task<IReadOnlyList<Approval>> TryReadPendingApprovalsAsync()
    {
        try
        {
            return [.. await Get<IApprovals>("approvals/index").Pending()];
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
            var compute = await Get<ICompute>("compute").Read();
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
            };
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
        var updated = await Get<IUserSettings>("settings").SetVariable(key, val);
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
            await Get<ICompute>("compute").SetBudget(cap);
            next = await ReadComputeSummaryAsync(next);
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
        var snapshot = await PortableBrainArchive.ExportAsync(brainId, Get<IUserSettings>("settings"), Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        await UpdateAsync(State.RecordExportedArchive(PortableBrainArchive.ToJson(snapshot), snapshot.Units.Count));
    }

}
