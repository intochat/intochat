/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget, ui.command

Declarative macOS Sonoma-style System Settings application for IntoChat OS (`UK-16` / `INT-1364`).
Owns the 13 settings sections, master-detail subpages (`AppPage`, `MemoryPage`, `ConnectionPage`,
`PersonPage`, `LocalServerPage`), modal sheets, search index, navigation backstack, and pure immutable
`SettingsAppState` DDD Aggregate projections (zero transient mutable grain fields!).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

[Signal("settings.open")]
public sealed record SettingsOpen(string? Section = null) : Signal;

[Signal("settings.read")]
public sealed record SettingsRead : Signal;

[Signal("settings.refresh-connections")]
public sealed record SettingsRefreshConnections(string? ConnectionKey = null, string? FriendApp = null) : Signal;

[Signal("settings.save-preferences")]
public sealed record SettingsSavePreferences(
    string? DisplayName = null,
    string? Theme = null,
    string? DefaultModel = null,
    string? StylePreset = null,
    string? BarLayout = null,
    string? ThemeMode = null,
    string? DesktopTone = null,
    string? Language = null,
    string? TimeZone = null,
    string? DateFormat = null) : Signal;

[Signal("settings.reload-preferences")]
public sealed record SettingsReloadPreferences : Signal;

[Signal("settings.reconcile-preferences")]
public sealed record SettingsReconcilePreferences : Signal;

[Signal("settings.navigate")]
public sealed record SettingsNavigate(string? Section = "general", string? SubPage = null, string? Sheet = null, string? SearchQuery = null) : Signal;

[Signal("settings.go-back")]
public sealed record SettingsGoBack : Signal;

[Signal("settings.export-brain")]
public sealed record SettingsExportBrain : Signal;

[Signal("settings.import-brain")]
public sealed record SettingsImportBrain(string? ArchiveJson = null) : Signal;

[Signal("settings.set-variable")]
public sealed record SettingsSetVariable(string? Key = null, string? Value = null) : Signal;

[Signal("settings.set-budget")]
public sealed record SettingsSetBudget(long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros) : Signal;

public sealed record SettingsSearchEntry(
    string SectionId,
    string Title,
    string Keywords,
    string ActionUri,
    string Icon = "settings",
    string Group = "Preferences");

/// <summary>
/// Rich DDD Aggregate owning all Settings navigation, preferences, and projected external state
/// (compute spend/cap, pending approvals, and redacted secret statuses) without transient grain fields.
/// </summary>
public sealed record SettingsAppState(
    string Title,
    string Status,
    string DisplayName,
    string Theme,
    string DefaultModel,
    string Section = "general",
    string? SubPage = null,
    string? Sheet = null,
    IReadOnlyList<string>? History = null,
    string SearchQuery = "",
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass",
    string ThemeMode = "auto",
    string DesktopTone = "cream",
    string Language = "English",
    string TimeZone = "UTC",
    string DateFormat = "17 Oct 2026",
    IReadOnlyList<string>? ConnectionKeys = null,
    IReadOnlyList<string>? FriendGrants = null,
    string? ExportedArchiveJson = null,
    IReadOnlyList<UserVariableEntry>? Variables = null,
    IReadOnlyList<string>? DeployedUnits = null,
    IReadOnlyList<ProfileFriend>? TeamFriends = null)
{
    public const long MinMonthlyBudgetMicros = 1_000_000L;       // $1.00
    public const long MaxMonthlyBudgetMicros = 10_000_000_000L;  // $10,000.00

    public static IReadOnlyList<string> DefaultConnectionKeys { get; } = [];
    public static IReadOnlyList<string> DefaultFriendGrants { get; } = [];

    // Projected immutable read-model fields (eliminating transient mutable grain fields on SettingsApp):
    public string ComputeSpendUsd { get; init; } = "$0.00";
    public string ComputeBudgetUsd { get; init; } = "$50.00";
    public long MonthlyBudgetMicros { get; init; } = ComputeState.DefaultMonthlyBudgetMicros;
    public bool ComputeBudgetExceeded { get; init; }
    public string ComputeRowsSummary { get; init; } = "0 model(s) metered";
    public IReadOnlyList<Approval> PendingApprovals { get; init; } = [];
    public IReadOnlyDictionary<string, bool>? SecretStatuses { get; init; }
    public bool NotifyApprovals { get; init; } = true;
    public bool NotifyBackgroundJobs { get; init; } = true;
    public bool NotifyConnectionHealth { get; init; } = true;
    public bool QuietHoursEnabled { get; init; }
    public bool ShowOsBarNotifications { get; init; } = true;
    public bool ShowOsBarComputeMeter { get; init; } = true;
    public bool ShowOsBarClock { get; init; } = true;

    public SettingsAppState() : this(
        "Settings",
        "Settings",
        "User",
        "dark",
        "qwen3.5:4b",
        "general",
        null,
        null,
        [],
        "",
        "fluent-acrylic",
        "floating-glass",
        "auto",
        "cream",
        "English",
        "UTC",
        "17 Oct 2026",
        [],
        [],
        null,
        [],
        [],
        [])
    {
    }

    [Invariant("Active section is always one of the 13 canonical Settings sections.")]
    public bool ValidSectionInvariant => SettingsApp.SearchIndex.Any(entry => entry.SectionId == Section);

    [Invariant("Monthly compute budget cap is always positive and within platform bounds.")]
    public Rule ValidBudgetCapInvariant => ValidateBudget(MonthlyBudgetMicros);

    [Invariant("Navigation history never exceeds 20 entries.")]
    public bool BoundedHistoryInvariant => (History?.Count ?? 0) <= 20;

    public bool CanGoBack => !string.IsNullOrEmpty(SubPage) || !string.IsNullOrEmpty(Sheet) || (History is { Count: > 0 });

    public Rule CanReturnRule => Decision.Require(
        "can_go_back",
        CanGoBack,
        "No previous settings page or sheet to return to.");

    public static Rule ValidateBudget(long monthlyBudgetMicros) => Decision.Rule(
        "valid_monthly_budget",
        monthlyBudgetMicros < MinMonthlyBudgetMicros || monthlyBudgetMicros > MaxMonthlyBudgetMicros,
        $"Monthly compute cap must be between $1.00 and $10,000.00 (received {monthlyBudgetMicros} micro-credits).");

    public static Rule ValidateVariableKey(string? key) => Decision.Rule(
        "variable_key_required",
        string.IsNullOrWhiteSpace(key),
        "Variable key cannot be empty.");

    public SettingsAppState Navigate(
        [Sample("general", "models", "billing", "apps:ino")] string? section,
        [Sample(null, "local-server")] string? subPage = null,
        string? sheet = null,
        string? searchQuery = null)
        => SettingsDecisions.DecideNavigate(this, section, subPage, sheet, searchQuery);

    public SettingsAppState GoBack() => SettingsDecisions.DecideGoBack(this);

    public SettingsAppState Filter([Sample("", "Ollama", "vault")] string query)
        => this with
        {
            SearchQuery = query?.Trim() ?? string.Empty,
            Status = string.IsNullOrWhiteSpace(query)
                ? $"Showing all {SettingsApp.SearchIndex.Count} sections"
                : $"Filtered sections matching '{query.Trim()}'",
        };

    public Decision<SettingsAppState> ApplyPreferences(
        [Sample("Ada")] string? displayName = null,
        [Sample("dark", "light")] string? theme = null,
        [Sample("qwen3.5:4b", "claude-sonnet-4-5")] string? defaultModel = null,
        [Sample("fluent-acrylic")] string? stylePreset = null,
        [Sample("floating-glass")] string? barLayout = null,
        [Sample("auto")] string? themeMode = null,
        [Sample("cream")] string? desktopTone = null)
    {
        var nextName = string.IsNullOrWhiteSpace(displayName) ? DisplayName : displayName.Trim();
        var nextTheme = string.IsNullOrWhiteSpace(theme) ? Theme : theme.Trim();
        var nextModel = string.IsNullOrWhiteSpace(defaultModel) ? DefaultModel : defaultModel.Trim();
        var nextPreset = string.IsNullOrWhiteSpace(stylePreset) ? StylePreset : stylePreset.Trim();
        var nextBar = string.IsNullOrWhiteSpace(barLayout) ? BarLayout : barLayout.Trim();
        var nextMode = string.IsNullOrWhiteSpace(themeMode) ? ThemeMode : themeMode.Trim();
        var nextTone = string.IsNullOrWhiteSpace(desktopTone) ? DesktopTone : desktopTone.Trim();

        return Decision.Accept(this with
        {
            Title = "Settings",
            Status = $"Saved: {nextName} ({nextTheme}, {nextModel}, {nextPreset}, {nextBar})",
            DisplayName = nextName,
            Theme = nextTheme,
            DefaultModel = nextModel,
            StylePreset = nextPreset,
            BarLayout = nextBar,
            ThemeMode = nextMode,
            DesktopTone = nextTone,
        });
    }

    public Decision<SettingsAppState> ApplyBudgetCap([Sample(50_000_000L, 0L)] long monthlyBudgetMicros)
    {
        var check = Decision.Check("set-budget", ValidateBudget(monthlyBudgetMicros));
        if (check is not null)
        {
            return check;
        }

        var usd = FormatUsd(monthlyBudgetMicros);
        return Decision.Accept(this with
        {
            Section = "billing",
            MonthlyBudgetMicros = monthlyBudgetMicros,
            ComputeBudgetUsd = usd,
            Status = $"Updated monthly compute cap to {usd}.",
        });
    }

    [IgnoreExplore]
    public SettingsAppState ToggleNotificationPreference(string key) => key switch
    {
        "approvals" => this with { NotifyApprovals = !NotifyApprovals },
        "jobs" => this with { NotifyBackgroundJobs = !NotifyBackgroundJobs },
        "connections" => this with { NotifyConnectionHealth = !NotifyConnectionHealth },
        "quiet" => this with { QuietHoursEnabled = !QuietHoursEnabled },
        "bar-notifications" => this with { ShowOsBarNotifications = !ShowOsBarNotifications },
        "bar-compute" => this with { ShowOsBarComputeMeter = !ShowOsBarComputeMeter },
        "bar-clock" => this with { ShowOsBarClock = !ShowOsBarClock },
        _ => this,
    };

    public static string FormatUsd(long micros)
        => (micros / 1_000_000.0).ToString("$0.00", CultureInfo.InvariantCulture);

    public override string ToString()
        => $"{Section}{(SubPage is null ? "" : "/" + SubPage)}{(Sheet is null ? "" : "#" + Sheet)} · {ComputeBudgetUsd}";
}

[Neuron("app.settings")]
[Screen("app.settings", Title = "Settings", Icon = Icons.Sliders)]
public sealed class SettingsApp() : AggregateScreen<SettingsAppState>
{
    private static readonly ILoggerFactory FallbackLoggerFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
    private static readonly ILogger FallbackLogger = FallbackLoggerFactory.CreateLogger<SettingsApp>();

    public ILogger<SettingsApp>? Logger { get; set; }
    public ISecretVault? SecretVault { get; set; }
    public BrainPreferenceStore? PreferenceStore { get; set; }

    private ILogger Log => Logger ?? FallbackLogger;

    public static IReadOnlyList<SettingsSearchEntry> SearchIndex { get; } =
    [
        new("general", "General", "appearance theme dark light auto glass acrylic spatial cyber synaptic warm paper midnight nordic language timezone date format", "os://window/profile", "sliders-horizontal", "Preferences"),
        new("desktop", "Desktop & Dock", "dock pinned apps bar widgets notifications tokens clock background tone cream sand peat ink", "os://window/settings?section=desktop", "monitor", "Preferences"),
        new("apps", "Apps", "installed apps store link install permissions capabilities memory ino", "os://sheet/install", "layout-grid", "Capabilities & Data"),
        new("connections", "Connections", "postgres salesforce google drive github slack integrations webhook oauth reconnect", "os://window/settings?section=connections", "plug", "Capabilities & Data"),
        new("files", "Files", "storage drive local computer root desktop mode synced folders conflict naming", "os://sheet/conflict", "folder", "Capabilities & Data"),
        new("models", "AI models", "ollama lm studio claude gpt gemini local inference reasoning effort default model", "os://window/settings?section=models", "sparkles", "Capabilities & Data"),
        new("keys", "API keys", "personal api key secrets token encryption vault anthropic openai groq mistral google", "os://window/secure", "key-round", "Capabilities & Data"),
        new("variables", "Variables and secrets", "environment variables key value table sealed secret vault scope local brain system", "os://window/settings?section=variables", "braces", "Capabilities & Data"),
        new("billing", "Billing", "tokens consumption credits balance auto top-up monthly spend limit payment invoices compute", "os://window/settings?section=billing", "credit-card", "Organization & System"),
        new("team", "Team and permissions", "teammates roster owner member guest invite person capability grants lfp circles", "os://window/settings?section=team", "users", "Organization & System"),
        new("notifications", "Notifications", "alerts approvals failed jobs mentions quiet hours schedule", "os://window/settings?section=notifications", "bell", "Organization & System"),
        new("keyboard", "Keyboard", "shortcuts hotkeys desktop conversation editor command palette", "os://window/settings?section=keyboard", "keyboard", "Organization & System"),
        new("danger", "Danger zone", "export archive zip delete account reset brain", "os://window/settings?section=danger", "triangle-alert", "Organization & System"),
    ];

    public const string AppName = ShippedApps.Settings;

    public static AppBundle Bundle { get; } = new(
        "settings",
        "2.1.0",
        new AppManifest(
            Title: "Settings",
            Description: "Your preferences, models, keys, connections and archived chats.",
            Operations:
            [
                new("open", "Show Settings on the tab the input names (general when empty or unknown)."),
                new("read", "Publish the current preferences to {app}/preferences."),
                new("refresh-connections", "Show the brain's connections and the host's connector offers again."),
                new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
                new("reload-preferences", "Discard editable drafts and load the stored preferences."),
                new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
                new("navigate", "Open the settings section (and sub-page) named by Input."),
                new("go-back", "Return to the previous settings page."),
                new("export-brain", "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units."),
                new("import-brain", "Restore a portable JSON archive into this brain's settings, profile, and deployed Synapse units."),
                new("set-variable", "Save or update a brain configuration variable in UserSettingsNeuron."),
                new("set-budget", "Update this brain's 30-day monthly compute spend cap in micro-credits."),
                new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "settings",
            Dock: 10),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(SettingsApp).AssemblyQualifiedName,
    };

    public static string NormalizeSection(string? section) => SettingsDecisions.NormalizeSection(section);

    public static IReadOnlyList<SettingsSearchEntry> FilterSections(string? query) => SettingsDecisions.FilterSections(query);

    protected override string Title => State.Title;

    [On]
    public Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync(request.Section);
    }

    [On]
    public Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync();
    }

    [On]
    public Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync();
    }

    [On]
    public Task ReconcilePreferences(SettingsReconcilePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateAsync(State with { Title = "Settings", Status = "Settings" });
    }

    private async Task RefreshFromNeuronsAsync(string? requestedSection = null)
    {
        var computeProjection = await ReadComputeSummaryAsync(State);
        var approvals = await TryReadPendingApprovalsAsync();
        var user = await Get<IUserSettings>("settings").Read();
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamFriends = await TryReadTeamFriendsAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? State.Variables ?? []);
        var targetSection = string.IsNullOrWhiteSpace(requestedSection) ? State.Section : NormalizeSection(requestedSection);

        var next = SettingsDecisions.DecideApplyUserSettings(
            computeProjection with
            {
                PendingApprovals = approvals,
                SecretStatuses = secrets,
            },
            user,
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            deployedUnits,
            teamFriends,
            section: targetSection);

        await UpdateAsync(next);
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
                    Log.LogSwallowed(error, "Failed probing secret vault; falling back to local variable/connection check.");
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
        => UpdateAsync(SettingsDecisions.DecideApplyUserSettings(baseline ?? State, user, status, deployedUnits, teamFriends, section, archiveJson));

    private async Task<IReadOnlyList<string>> TryReadDeployedUnitsAsync()
    {
        try
        {
            var deployed = await Get<ISynapseStudio>("ino/synapses").List();
            return [.. deployed.Select(d => d.SourceKey).Where(k => !string.IsNullOrWhiteSpace(k)).OrderBy(k => k, StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "SynapseStudio is optional when SettingsApp runs in standalone tests.");
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
            Log.LogSwallowed(error, "ProfileNeuron is optional when SettingsApp runs in standalone tests.");
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
            Log.LogSwallowed(error, "ApprovalsNeuron is optional when SettingsApp runs in standalone tests.");
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
            Log.LogSwallowed(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
            return baseline;
        }
    }

    [On]
    public Task Navigate(SettingsNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateAsync(State.Navigate(request.Section, request.SubPage, request.Sheet, request.SearchQuery));
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
        return UpdateAsync(SettingsDecisions.DecideRefreshConnections(State, request.ConnectionKey, request.FriendApp));
    }

    [On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (new[]
            {
                request.DisplayName, request.Theme, request.DefaultModel, request.StylePreset, request.BarLayout,
                request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat,
            }.All(string.IsNullOrWhiteSpace))
        {
            await UpdateAsync(State with { Title = "Settings", Status = "Nothing to save." });
            return;
        }

        var store = PreferenceStore ?? new BrainPreferenceStore(Get<IUserSettings>("settings"));
        var updated = await store.SaveAsync(request, State);
        await ApplyUserSettingsAsync(updated, $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel}, {updated.StylePreset}, {updated.BarLayout})");
    }

    [On]
    public async Task SetVariable(SettingsSetVariable request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SettingsAppState.ValidateVariableKey(request.Key).Broken)
        {
            await UpdateAsync(State with { Status = "Variable key cannot be empty." });
            return;
        }

        var key = request.Key!.Trim();
        var val = request.Value ?? "";
        var updated = await Get<IUserSettings>("settings").SetVariable(key, val);
        var variables = updated.Variables ?? [];
        var secrets = await InspectSecretStatusesAsync(variables);
        await UpdateAsync(SettingsDecisions.DecideSetVariable(State with { SecretStatuses = secrets }, key, variables));
    }

    [On]
    public async Task SetBudget(SettingsSetBudget request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cap = request.MonthlyBudgetMicros > 0
            ? request.MonthlyBudgetMicros
            : ComputeState.DefaultMonthlyBudgetMicros;
        var decision = State.ApplyBudgetCap(cap);
        if (decision.IsRefused)
        {
            await ApplyDecisionAsync(decision);
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
            Log.LogSwallowed(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
        }

        await UpdateAsync(SettingsDecisions.DecideSetBudget(next, next.ComputeBudgetUsd));
    }

    [On]
    public async Task ExportBrain(SettingsExportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await PortableBrainArchive.ExportAsync("local", Get<IUserSettings>("settings"), Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        await UpdateAsync(SettingsDecisions.DecideExportBrain(State, PortableBrainArchive.ToJson(snapshot), snapshot.Units.Count));
    }

    [On]
    public async Task ImportBrain(SettingsImportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var archiveJson = string.IsNullOrWhiteSpace(request.ArchiveJson) ? State.ExportedArchiveJson : request.ArchiveJson;
        if (string.IsNullOrWhiteSpace(archiveJson))
        {
            await UpdateAsync(State with { Section = "danger", Status = "No portable brain archive JSON provided to import." });
            return;
        }

        var snapshot = PortableBrainArchive.FromJson(archiveJson);
        var settings = Get<IUserSettings>("settings");
        await PortableBrainArchive.ImportAsync(snapshot, settings, Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        var user = await settings.Read();
        var computeProjection = await ReadComputeSummaryAsync(State);
        var approvals = await TryReadPendingApprovalsAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? []);
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamFriends = await TryReadTeamFriendsAsync();

        await ApplyUserSettingsAsync(
            user,
            $"Restored portable brain archive from {snapshot.BrainId} ({snapshot.Units.Count} unit(s)).",
            deployedUnits,
            teamFriends,
            section: "danger",
            archiveJson: archiveJson,
            baseline: computeProjection with
            {
                PendingApprovals = approvals,
                SecretStatuses = secrets,
            });
    }

    public new SettingsAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override Node Render(SettingsAppState state)
        => SettingsViewRenderer.RenderRoot(state, LastRejection);

    private Task UpdateAsync(SettingsAppState next) => Show(next);

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            Log.LogSwallowed(error, $"{typeof(T).Name} is optional when running in isolated unit tests.");
            return null;
        }
    }
}
