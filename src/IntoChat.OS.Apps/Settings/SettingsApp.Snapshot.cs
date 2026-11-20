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
            AvailableModels = modelInfo.Options,
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

    internal static IReadOnlyList<SettingsModelOption> CatalogFallbackOptions() =>
    [
        .. KnownModels.All
            .Where(static m => m.Can.HasFlag(Abilities.Tools) && (m.Provider != AiProvider.Ollama || m.Id == KnownModels.Defaults.Device[Role.Chat]))
            .Select(static m =>
            {
                var visual = VisualCatalog.ForModel(m.Id.Value, m.Provider.ToString());
                return new SettingsModelOption(m.Id.Value, visual.Label, true, visual.Category);
            }),
    ];

    private async Task<(IReadOnlyList<SettingsModelOption> Options, string? AutomaticModel, string LocalEndpoint)> TryReadModelCatalogAsync(string? personModel)
    {
        BrainId brain = default;
        try
        {
            brain = Id.Brain;
        }
        catch (InvalidOperationException error)
        {
            Log.LogDebug(error, "Settings brain id is unavailable during standalone test activation.");
            return (State.AvailableModels is { Count: > 0 } ? State.AvailableModels : CatalogFallbackOptions(), null, State.LocalOllamaEndpoint);
        }

        if (string.IsNullOrWhiteSpace(brain.Value))
        {
            return (State.AvailableModels is { Count: > 0 } ? State.AvailableModels : CatalogFallbackOptions(), null, State.LocalOllamaEndpoint);
        }

        try
        {
            var catalog = Get<IModelCatalog>(IModelCatalog.Key(brain.Value));
            var cards = await catalog.List(Abilities.Tools);
            var availability = (await catalog.Availability())
                .ToDictionary(static a => a.Model.Value, StringComparer.OrdinalIgnoreCase);

            var deduped = new Dictionary<string, SettingsModelOption>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                var id = card.Id.Value;
                var available = availability.TryGetValue(id, out var avail) && avail.Ready;
                var reason = available ? null : avail?.Reason;
                var providerLabel = VisualCatalog.ForProvider(card.Provider.ToString()).Category;
                var option = new SettingsModelOption(id, card.Name, available, providerLabel, reason);
                if (!deduped.TryGetValue(id, out var existing) || (!existing.Available && option.Available))
                {
                    deduped[id] = option;
                }
            }

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

            IReadOnlyList<SettingsModelOption> resolvedOptions = deduped.Count > 0 ? [.. deduped.Values] : CatalogFallbackOptions();
            return (resolvedOptions, autoModel, IntoChatOSNames.LocalOllamaEndpoint);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IModelCatalog is optional when SettingsApp runs in standalone tests.");
            return (State.AvailableModels is { Count: > 0 } ? State.AvailableModels : CatalogFallbackOptions(), null, State.LocalOllamaEndpoint);
        }
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
                AvailableModels = modelInfo.Options,
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
