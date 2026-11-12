/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget

Declarative macOS System Settings-style application for IntoChat OS.
Owns the 13 settings sections, master-detail subpages (AppPage, MemoryPage, ConnectionPage, PersonPage, LocalServerPage),
modal sheets, search index, and navigation backstack. Preferences read and persist through UserSettingsNeuron (IUserSettings).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

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
public sealed record SettingsSetBudget(long MonthlyBudgetMicros = IntoChat.OS.Modules.Flutter.Compute.ComputeState.DefaultMonthlyBudgetMicros) : Signal;

public sealed record SettingsSearchEntry(string SectionId, string Title, string Keywords, string ActionUri, string Icon = "settings", string Group = "Preferences");

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
    Node? OverrideBody = null,
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
    public static IReadOnlyList<string> DefaultConnectionKeys { get; } = [];
    public static IReadOnlyList<string> DefaultFriendGrants { get; } = [];

    public SettingsAppState() : this(
        "Settings", "Settings", "User", "dark", "qwen3.5:4b",
        "general", null, null, [], "", null,
        "fluent-acrylic", "floating-glass", "auto", "cream", "English", "UTC", "17 Oct 2026",
        [], [], null, [], [], [])
    {
    }
}

[Neuron("app.settings")]
public sealed class SettingsApp() : Screen<SettingsAppState>
{
    private string _computeSpendUsd = "$0.00";
    private string _computeBudgetUsd = "$50.00";
    private bool _computeBudgetExceeded;
    private string _computeRowsSummary = "0 model(s) metered";
    private IntoChat.OS.Modules.Apps.Approvals.Approval[] _pendingApprovals = [];

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

    public static string NormalizeSection(string? section) => (section ?? "general").Trim().ToLowerInvariant() switch
    {
        "appearance" or "profile" => "general",
        "compute" => "billing",
        "circles" or "friends" => "team",
        "modules" => "apps",
        "archived" => "files",
        var s when SearchIndex.Any(entry => entry.SectionId == s) => s,
        _ => "general",
    };

    public static IReadOnlyList<SettingsSearchEntry> FilterSections(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SearchIndex;
        }
        var q = query.Trim();
        return [.. SearchIndex.Where(entry =>
            entry.SectionId.Contains(q, StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || entry.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase))];
    }

    [On]
    public Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync();
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
        return UpdateAsync(State with { Title = "Settings", Status = "Settings", OverrideBody = null });
    }

    private async Task RefreshFromNeuronsAsync()
    {
        await TryRefreshComputeSummaryAsync();
        await TryRefreshPendingApprovalsAsync();
        var user = await Get<IUserSettings>("settings").Read();
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamFriends = await TryReadTeamFriendsAsync();
        await ApplyUserSettingsAsync(user, $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", deployedUnits, teamFriends);
    }

    private Task ApplyUserSettingsAsync(
        UserSettingsState user,
        string status,
        IReadOnlyList<string>? deployedUnits = null,
        IReadOnlyList<ProfileFriend>? teamFriends = null,
        string? section = null,
        string? archiveJson = null)
        => UpdateAsync(State with
        {
            Title = "Settings",
            Status = status,
            DisplayName = user.DisplayName,
            Theme = user.Theme,
            DefaultModel = user.DefaultModel,
            Section = section ?? State.Section,
            OverrideBody = null,
            StylePreset = user.StylePreset,
            BarLayout = user.BarLayout,
            ThemeMode = user.ThemeMode,
            DesktopTone = user.DesktopTone,
            Language = user.Language,
            TimeZone = user.TimeZone,
            DateFormat = user.DateFormat,
            ExportedArchiveJson = archiveJson ?? State.ExportedArchiveJson,
            Variables = user.Variables ?? [],
            DeployedUnits = deployedUnits ?? State.DeployedUnits ?? [],
            TeamFriends = teamFriends ?? State.TeamFriends ?? [],
        });

    private async Task<IReadOnlyList<string>> TryReadDeployedUnitsAsync()
    {
        try
        {
            var deployed = await Get<ISynapseStudio>("ino/synapses").List();
            return [.. deployed.Select(d => d.SourceKey).Where(k => !string.IsNullOrWhiteSpace(k)).OrderBy(k => k, StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "SynapseStudio is optional when SettingsApp runs in standalone tests.");
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
            Swallow(error, "ProfileNeuron is optional when SettingsApp runs in standalone tests.");
            return State.TeamFriends ?? [];
        }
    }

    private async Task TryRefreshPendingApprovalsAsync()
    {
        try
        {
            _pendingApprovals = [.. await Get<IntoChat.OS.Modules.Apps.Approvals.IApprovals>("approvals/index").Pending()];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "ApprovalsNeuron is optional when SettingsApp runs in standalone tests.");
        }
    }

    private async Task TryRefreshComputeSummaryAsync()
    {
        try
        {
            var compute = await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Read();
            _computeSpendUsd = (compute.SpentMicros / 1_000_000.0).ToString("$0.00", CultureInfo.InvariantCulture);
            _computeBudgetUsd = (compute.MonthlyBudgetMicros / 1_000_000.0).ToString("$0.00", CultureInfo.InvariantCulture);
            _computeBudgetExceeded = compute.BudgetExceeded;
            _computeRowsSummary = $"{compute.Rows.Length} model(s) metered";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
        }
    }

    [On]
    public Task Navigate(SettingsNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (rawSection, rawSubPage, rawSheet) = (request.Section, request.SubPage, request.Sheet);
        if (!string.IsNullOrWhiteSpace(rawSection)
            && rawSection.Contains(':', StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(rawSubPage)
            && string.IsNullOrWhiteSpace(rawSheet))
        {
            var segments = rawSection.Split(':', 3, StringSplitOptions.TrimEntries);
            rawSection = segments[0];
            rawSubPage = segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1]) ? segments[1] : null;
            rawSheet = segments.Length > 2 && !string.IsNullOrWhiteSpace(segments[2]) ? segments[2] : null;
        }

        var targetSection = string.IsNullOrWhiteSpace(rawSection) ? State.Section : NormalizeSection(rawSection);
        var targetSubPage = string.IsNullOrWhiteSpace(rawSubPage) ? null : rawSubPage.Trim();
        var targetSheet = string.IsNullOrWhiteSpace(rawSheet) ? null : rawSheet.Trim();

        var history = (State.History ?? []).ToList();
        var currentPath = string.IsNullOrEmpty(State.SubPage) ? State.Section : $"{State.Section}/{State.SubPage}";
        var nextPath = string.IsNullOrEmpty(targetSubPage) ? targetSection : $"{targetSection}/{targetSubPage}";
        if (currentPath != nextPath && !string.IsNullOrEmpty(currentPath))
        {
            history.Add(currentPath);
            if (history.Count > 20)
            {
                history.RemoveAt(0);
            }
        }

        return UpdateAsync(State with
        {
            Title = $"Settings · {targetSection}",
            Status = $"Navigated to {targetSection}" + (targetSubPage != null ? $" / {targetSubPage}" : ""),
            Section = targetSection,
            SubPage = targetSubPage,
            Sheet = targetSheet,
            SearchQuery = request.SearchQuery ?? State.SearchQuery,
            History = history,
            OverrideBody = null,
        });
    }

    [On]
    public Task GoBack(SettingsGoBack request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var history = (State.History ?? []).ToList();
        if (history.Count == 0)
        {
            return UpdateAsync(State with { Sheet = null, SubPage = null });
        }
        var last = history[^1];
        history.RemoveAt(history.Count - 1);
        var parts = last.Split('/', 2);
        var (section, subPage) = (parts[0], parts.Length > 1 ? parts[1] : null);
        return UpdateAsync(State with
        {
            Title = $"Settings · {section}",
            Status = $"Back to {section}" + (subPage != null ? $" / {subPage}" : ""),
            Section = section,
            SubPage = subPage,
            Sheet = null,
            History = history,
            OverrideBody = null,
        });
    }

    [On]
    public Task RefreshConnections(SettingsRefreshConnections request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keys = (State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys).ToList();
        if (!string.IsNullOrWhiteSpace(request.ConnectionKey) && !keys.Contains(request.ConnectionKey, StringComparer.Ordinal))
        {
            keys.Add(request.ConnectionKey);
        }
        var grants = (State.FriendGrants ?? SettingsAppState.DefaultFriendGrants).ToList();
        if (!string.IsNullOrWhiteSpace(request.FriendApp))
        {
            var grantEntry = $"sdk -> {request.FriendApp} ({request.ConnectionKey ?? "storage@1"})";
            if (!grants.Contains(grantEntry, StringComparer.Ordinal))
            {
                grants.Add(grantEntry);
            }
        }
        return UpdateAsync(State with { Title = "Settings", Status = "Connections", OverrideBody = null, ConnectionKeys = keys, FriendGrants = grants });
    }

    [On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (new[] { request.DisplayName, request.Theme, request.DefaultModel, request.StylePreset, request.BarLayout, request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat }.All(string.IsNullOrWhiteSpace))
        {
            await UpdateAsync(State with { Title = "Settings", Status = "Nothing to save.", OverrideBody = null });
            return;
        }

        var settings = Get<IUserSettings>("settings");
        var updated = await settings.SetProfile(
            request.DisplayName ?? State.DisplayName,
            request.Theme ?? State.Theme,
            request.DefaultModel ?? State.DefaultModel);
        if (!string.IsNullOrWhiteSpace(request.StylePreset) || !string.IsNullOrWhiteSpace(request.BarLayout))
        {
            updated = await settings.SetUiIdentity(request.StylePreset ?? State.StylePreset, request.BarLayout ?? State.BarLayout);
        }
        if (new[] { request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat }.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            updated = await settings.SetPreferences(
                request.ThemeMode ?? State.ThemeMode,
                request.DesktopTone ?? State.DesktopTone,
                request.Language ?? State.Language,
                request.TimeZone ?? State.TimeZone,
                request.DateFormat ?? State.DateFormat);
        }

        await ApplyUserSettingsAsync(updated, $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel}, {updated.StylePreset}, {updated.BarLayout})");
    }

    [On]
    public async Task SetVariable(SettingsSetVariable request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            await UpdateAsync(State with { Status = "Variable key cannot be empty.", OverrideBody = null });
            return;
        }
        var updated = await Get<IUserSettings>("settings").SetVariable(request.Key.Trim(), request.Value ?? "");
        await UpdateAsync(State with { Section = "variables", Status = $"Saved variable {request.Key.Trim()}.", Variables = updated.Variables ?? [], OverrideBody = null });
    }

    [On]
    public async Task SetBudget(SettingsSetBudget request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cap = request.MonthlyBudgetMicros > 0
            ? request.MonthlyBudgetMicros
            : IntoChat.OS.Modules.Flutter.Compute.ComputeState.DefaultMonthlyBudgetMicros;
        try
        {
            await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").SetBudget(cap);
            await TryRefreshComputeSummaryAsync();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
            _computeBudgetUsd = (cap / 1_000_000.0).ToString("$0.00", CultureInfo.InvariantCulture);
        }
        await UpdateAsync(State with { Section = "billing", Status = $"Updated monthly compute cap to {_computeBudgetUsd}.", OverrideBody = null });
    }

    [On]
    public async Task ExportBrain(SettingsExportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await PortableBrainArchive.ExportAsync("local", Get<IUserSettings>("settings"), Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        await UpdateAsync(State with
        {
            Section = "danger",
            Sheet = "export-archive",
            Status = $"Exported portable brain archive ({snapshot.Units.Count} unit(s)).",
            ExportedArchiveJson = PortableBrainArchive.ToJson(snapshot),
            OverrideBody = null,
        });
    }

    [On]
    public async Task ImportBrain(SettingsImportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var archiveJson = string.IsNullOrWhiteSpace(request.ArchiveJson) ? State.ExportedArchiveJson : request.ArchiveJson;
        if (string.IsNullOrWhiteSpace(archiveJson))
        {
            await UpdateAsync(State with { Section = "danger", Status = "No portable brain archive JSON provided to import.", OverrideBody = null });
            return;
        }

        var snapshot = PortableBrainArchive.FromJson(archiveJson);
        var settings = Get<IUserSettings>("settings");
        await PortableBrainArchive.ImportAsync(snapshot, settings, Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        var user = await settings.Read();
        await ApplyUserSettingsAsync(
            user,
            $"Restored portable brain archive from {snapshot.BrainId} ({snapshot.Units.Count} unit(s)).",
            await TryReadDeployedUnitsAsync(),
            await TryReadTeamFriendsAsync(),
            section: "danger",
            archiveJson: archiveJson);
    }

    public new SettingsAppState State => base.State;
    public View Render() => Render(State);
    protected override View Render(SettingsAppState state)
    {
        if (state.OverrideBody is not null)
        {
            return new(state.Title, state.OverrideBody);
        }

        var section = NormalizeSection(state.Section);
        var sectionEntry = SearchIndex.FirstOrDefault(s => s.SectionId == section) ?? SearchIndex[0];
        var nodes = new List<Node> { RenderHeader(sectionEntry) };
        if (!string.IsNullOrWhiteSpace(state.Sheet))
        {
            nodes.Add(RenderModalSheet(state.Sheet!));
        }
        nodes.Add(RenderSectionBody(section, state.SubPage));
        return new(state.Title, new Column([.. nodes]) { Gap = 14 });
    }

    private Node RenderHeader(SettingsSearchEntry sectionEntry)
    {
        var canGoBack = !string.IsNullOrEmpty(State.SubPage) || (State.History is { Count: > 0 });
        var breadcrumb = string.IsNullOrEmpty(State.SubPage) ? sectionEntry.Title : $"{sectionEntry.Title} / {State.SubPage}";
        var actions = new List<Node>();
        if (canGoBack)
        {
            actions.Add(ActionButton("back", "Back", AppSignals.Emit("settings", "go-back"), icon: "arrow-left"));
        }
        actions.Add(new TagPill(sectionEntry.Group, "neutral", sectionEntry.Icon));
        actions.Add(new TagPill($"{State.StylePreset} · {State.ThemeMode}", "accent", "palette"));
        actions.Add(new Text(State.Status) { Tone = "muted", Tabular = true });

        return new Surface([new Heading(breadcrumb, Level: 2, Subtitle: sectionEntry.Keywords), new Row([.. actions]) { Gap = 8 }], Kind: SurfaceKind.Acrylic, Title: breadcrumb);
    }

    private Node RenderModalSheet(string sheet) => new Surface(
        [
            new Heading($"Sheet · {sheet}", Level: 3, Subtitle: "Modal configuration overlay (press Esc or Close to dismiss)."),
            new Text(sheet switch
            {
                "install-link" => "Paste a verified Smart App link (https://intochat.app/a/...) to inspect its manifest and capability requirements.",
                "add-connection" or "google-drive" => "Authorize an OAuth 2.0 or token-based integration through the zero-leakage Capability Broker.",
                "ollama-config" => "Configure local Ollama / LM Studio server endpoint (http://localhost:11434) and reasoning effort.",
                "invite" => "Invite a teammate to this brain and assign Owner, Member, or Guest circle permissions.",
                "export-archive" => State.ExportedArchiveJson ?? "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units.",
                "confirm-delete" => "Two-step confirmation required: deleting or resetting this brain permanently revokes all sessions.",
                _ => $"Active modal sheet: {sheet}",
            }),
            new Row([ActionButton("close-sheet", "Close Sheet", AppSignals.Emit("settings", "navigate", State.Section, State.SubPage ?? "", ""), icon: "x")]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: $"Modal Sheet · {sheet}");

    private Node RenderSectionBody(string section, string? subPage) => section switch
    {
        "desktop" => RenderDesktopSection(),
        "apps" => RenderAppsSection(subPage),
        "connections" => RenderConnectionsSection(subPage),
        "files" => RenderFilesSection(),
        "models" => RenderModelsSection(subPage),
        "keys" => RenderKeysSection(),
        "variables" => RenderVariablesSection(),
        "billing" => RenderBillingSection(),
        "team" => RenderTeamSection(subPage),
        "notifications" => RenderNotificationsSection(),
        "keyboard" => RenderKeyboardSection(),
        "danger" => RenderDangerSection(),
        _ => RenderGeneralSection(),
    };

    private Node RenderGeneralSection() => new Column(
    [
        new Grid(
            3,
            [
                Ui.StatCard("stat-preset", "Active UI Identity", State.StylePreset, State.BarLayout, BadgeTone.Accent, "palette"),
                Ui.StatCard("stat-model", "Default Brain Model", State.DefaultModel, "Local + Cloud", BadgeTone.Done, "cpu"),
                Ui.StatCard("stat-vault", "Zero-Leakage Vault", $"{(State.ConnectionKeys ?? []).Count} Connections", "Active", BadgeTone.Neutral, "key-round"),
            ],
            Gap: 12),
        new Surface(
            [
                new Heading("Appearance & Theme Mode", Level: 3, Subtitle: "How IntoChat looks and reads for you."),
                new Row(
                [
                    PrefButton("light", "Light", State.ThemeMode == "light", theme: "light", themeMode: "light", icon: "sun"),
                    PrefButton("dark", "Dark", State.ThemeMode == "dark", theme: "dark", themeMode: "dark", icon: "moon"),
                    PrefButton("auto", "Auto", State.ThemeMode == "auto", themeMode: "auto", icon: "sparkles"),
                ]) { Gap = 8 },
                new Heading("Theme & UI Identity Presets", Level: 3, Subtitle: "Switch live Acrylic glass presets, colour themes, and adaptive OS bar layouts."),
                new Row(
                    new (string Id, string Label)[]
                    {
                        ("fluent-acrylic", "Fluent Acrylic"), ("spatial-glass", "Spatial Glass"), ("cyber-synaptic", "Cyber Synaptic"),
                        ("warm-paper", "Warm Paper"), ("midnight", "Midnight"), ("nordic", "Nordic"),
                    }.Select(p => PrefButton(p.Id, p.Label, State.StylePreset == p.Id, preset: p.Id)).ToArray()) { Gap = 8 },
                new RecordCardNode(
                    "Locale & Regional Formatting",
                    State.Language,
                    [
                        new RecordFieldNode("Language", State.Language),
                        new RecordFieldNode("Time Zone", State.TimeZone),
                        new RecordFieldNode("Date Format", State.DateFormat),
                        new RecordFieldNode("Desktop Tone", State.DesktopTone),
                    ],
                    Subtitle: "General Preferences"),
            ],
            Kind: SurfaceKind.Acrylic,
            Title: "1 · General & Appearance"),
        new Surface(
            [
                new Heading("Quick OS Windows & Security Sheets", Level: 3, Subtitle: "Declarative os:// window and sheet protocol across Profile, Secure Vault, App Store & File Conflict."),
                new Row(
                [
                    IntentButton("open-profile-2fa", "Open Profile & 2FA", "os://window/profile", ButtonTone.Accent, "user"),
                    IntentButton("open-secure-vault", "Open Secure Vault", "os://window/secure", ButtonTone.Neutral, "key-round"),
                    IntentButton("install-app-by-link", "Install App by Link", "os://sheet/install", ButtonTone.Neutral, "package-plus"),
                    IntentButton("review-update-consent", "Review Update Consent", "os://sheet/reconsent", ButtonTone.Neutral, "shield-check"),
                    IntentButton("resolve-file-conflict", "Resolve File Conflict", "os://sheet/conflict", ButtonTone.Neutral, "git-compare"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "2 · Profile, Secure Vault & Modal Sheets"),
    ])
    { Gap = 12 };

    private Node RenderDesktopSection() => new Surface(
        [
            new Heading("Desktop Background Tone", Level: 3, Subtitle: "Select the ambient desktop canvas tone."),
            new Row(
                new (string Id, string Label)[] { ("cream", "Cream"), ("sand", "Sand"), ("peat", "Peat"), ("ink", "Ink") }
                    .Select(t => PrefButton(t.Id, t.Label, State.DesktopTone == t.Id, themeMode: State.ThemeMode, desktopTone: t.Id)).ToArray()) { Gap = 8 },
            new Heading("OS Bar Layout & Widgets", Level: 3, Subtitle: "Configure Dock pinned apps and status bar widgets."),
            new Row(
                new (string Id, string Label)[] { ("floating-glass", "Floating Glass"), ("compact-pill", "Compact Pill"), ("classic-bottom", "Classic Bottom") }
                    .Select(b => PrefButton(b.Id, b.Label, State.BarLayout == b.Id, barLayout: b.Id)).ToArray()) { Gap = 8 },
            new Switch("show-notifications-badge-in-os-bar", "Show Notifications badge in OS Bar", true),
            new Switch("show-live-token-compute-meter-in-os-bar", "Show live Token & Compute meter in OS Bar", true),
            new Switch("show-clock-schedule-calendar-in-os-bar", "Show Clock & Schedule calendar in OS Bar", true),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Desktop & Dock");

    private Node RenderAppsSection(string? subPage)
    {
        if (string.Equals(subPage, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return DetailSubPage(
                "Apps / MemoryPage",
                "MemoryPage · Ino Episodic & Semantic Memory",
                "Inspect and manage facts recalled by neuron://local/memory.",
                new RecordCardNode(
                    "Memory Neuron",
                    "neuron://local/memory",
                    [
                        new RecordFieldNode("Status", "Active · Recall top-3 episodes per turn"),
                        new RecordFieldNode("Retention Policy", "LFP-gated deletion (requires ApprovalCard)"),
                    ],
                    Subtitle: "Ino Memory Store"),
                "back-to-apps",
                "Back to Apps");
        }

        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return DetailSubPage(
                $"Apps / {subPage}",
                $"AppPage · {subPage}",
                $"Manifest, permissions, and sandbox isolation for neuron://local/apps/{subPage}.",
                new RecordCardNode(
                    "Installed Smart App",
                    subPage,
                    [
                        new RecordFieldNode("Neuron URI", $"neuron://local/apps/{subPage}"),
                        new RecordFieldNode("Write Gate", "LFP ApprovalCard enforced"),
                    ],
                    Subtitle: $"App Detail · {subPage}"),
                "back-to-apps",
                "Back to Apps");
        }

        var unitButtons = (State.DeployedUnits ?? [])
            .Select(alias => ActionButton($"inspect-unit-{alias}", $"Unit · {alias}", AppSignals.Emit("settings", "navigate", "apps", alias), icon: "cpu"))
            .ToArray();

        var children = new List<Node>
        {
            new Heading("Installed Smart Apps & Deployed Units", Level: 3, Subtitle: "Manage installed apps, deployed Synapse units, and inspect permissions."),
            new Row(
            [
                ActionButton("inspect-ino-app", "Inspect Ino App", AppSignals.Emit("settings", "navigate", "apps", "ino"), ButtonTone.Accent, "sparkles"),
                ActionButton("inspect-creator-app", "Inspect Creator App", AppSignals.Emit("settings", "navigate", "apps", "creator"), icon: "wand-sparkles"),
                ActionButton("ino-memorypage", "Ino MemoryPage", AppSignals.Emit("settings", "navigate", "apps", "memory"), icon: "brain"),
                ActionButton("install-from-link", "Install from Link", AppSignals.Emit("settings", "navigate", "apps", "", "install-link"), icon: "package-plus"),
            ]) { Gap = 8 },
        };
        if (unitButtons.Length > 0)
        {
            children.Add(new Row(unitButtons) { Gap = 8 });
        }
        return new Surface([.. children], Kind: SurfaceKind.Elevated, Title: "Apps");
    }

    private Node RenderConnectionsSection(string? subPage)
    {
        var keys = State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return new Surface(
                [
                    new Heading($"ConnectionPage · {subPage}", Level: 3, Subtitle: "OAuth scopes, health probe, and zero-token broker bindings."),
                    new RecordCardNode(
                        "Connection Endpoint",
                        subPage,
                        [
                            new RecordFieldNode("Neuron URI", $"neuron://local/connection/{subPage}"),
                            new RecordFieldNode("Broker Mode", "ICapabilityBroker · Zero-Token Credential Injection"),
                            new RecordFieldNode("Health", "Connected · Verified"),
                        ],
                        Subtitle: $"Connection Detail · {subPage}"),
                    new Row(
                    [
                        ActionButton("reconnect-oauth", "Reconnect OAuth", AppSignals.Emit("settings", "navigate", "connections", subPage, "add-connection"), ButtonTone.Accent, "refresh-cw"),
                        ActionButton("back-to-connections", "Back to Connections", AppSignals.Emit("settings", "go-back"), icon: "arrow-left"),
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Elevated,
                Title: $"Connections / {subPage}");
        }

        return new Surface(
            [
                new Heading("Connected Integrations & Capability Broker", Level: 3, Subtitle: "Accounts and services this brain can reach without exposing raw credentials."),
                new RecordCardNode(
                    "Capability Broker",
                    "Connections & Storage",
                    keys.Select((k, idx) => new RecordFieldNode($"Connection {idx + 1}", k)).ToArray(),
                    Subtitle: "Active Connections"),
                new Row(
                [
                    ActionButton("github-connection", "GitHub Connection", AppSignals.Emit("settings", "navigate", "connections", "github"), ButtonTone.Accent, "plug"),
                    ActionButton("google-drive-connection", "Google Drive Connection", AppSignals.Emit("settings", "navigate", "connections", "google-drive"), icon: "folder"),
                    ActionButton("add-connection", "Add Connection", AppSignals.Emit("settings", "navigate", "connections", "", "add-connection"), icon: "plus"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Connections");
    }

    private Node RenderFilesSection()
    {
        var keys = State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
        RecordFieldNode[] mounts =
        [
            new("IntoChat Storage", "neuron://local/local-cloud/main"),
            new("Local Computer Root", "neuron://local/disk.files"),
            .. keys.Select(static (k, idx) => new RecordFieldNode($"Connected Mount {idx + 1}", k)),
        ];

        return new Surface(
            [
                new Heading("Files & Hybrid Storage Locations", Level: 3, Subtitle: "Manage IntoChat cloud storage, connected mounts, and local disk roots."),
                new RecordCardNode("Mounted File Roots", "neuron://local/disk.files", mounts, Subtitle: "Storage Providers"),
                ActionButton("resolve-file-conflict-sheet", "Resolve File Conflict Sheet", AppSignals.Emit("settings", "navigate", "files", "", "conflict"), icon: "git-compare"),
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Files");
    }

    private Node RenderModelsSection(string? subPage)
    {
        if (string.Equals(subPage, "local-server", StringComparison.OrdinalIgnoreCase))
        {
            return DetailSubPage(
                "AI models / local-server",
                "Local Model Server · Ollama & LM Studio",
                "Configure local inference endpoint and reasoning effort.",
                new RecordCardNode(
                    "Local Inference Endpoint",
                    "http://localhost:11434",
                    [
                        new RecordFieldNode("Active Local Model", State.DefaultModel),
                        new RecordFieldNode("Reasoning Effort", "Medium · Zero-Telemetry Local Execution"),
                    ],
                    Subtitle: "Ollama Local Server"),
                "back-to-ai-models",
                "Back to AI Models");
        }

        return new Surface(
            [
                new Heading("Default Brain Model & Routing", Level: 3, Subtitle: "Select the default model for Ino and autonomous background neurons."),
                new Row(
                [
                    PrefButton("qwen3-5-4b-local", "qwen3.5:4b (Local)", State.DefaultModel == "qwen3.5:4b", model: "qwen3.5:4b"),
                    PrefButton("claude-sonnet-4-5", "claude-sonnet-4-5", State.DefaultModel == "claude-sonnet-4-5", model: "claude-sonnet-4-5"),
                    PrefButton("gpt-5", "gpt-5", State.DefaultModel == "gpt-5", model: "gpt-5"),
                ]) { Gap = 8 },
                new Row(
                [
                    ActionButton("local-server-ollama", "Local Server (Ollama)", AppSignals.Emit("settings", "navigate", "models", "local-server"), ButtonTone.Accent, "cpu"),
                    ActionButton("configure-endpoint-sheet", "Configure Endpoint Sheet", AppSignals.Emit("settings", "navigate", "models", "", "ollama-config"), icon: "sliders-horizontal"),
                    ActionButton("open-api-keys-section", "API Keys & Vault", AppSignals.Emit("settings", "navigate", "keys"), icon: "key-round"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "AI models");
    }

    private Node RenderKeysSection()
    {
        (string Provider, string Detail, string SecretName, string Connection, string ButtonId, string ButtonLabel)[] vaults =
        [
            ("Anthropic", "Used for Claude Sonnet & Opus models", "ANTHROPIC_API_KEY", "anthropic", "seal-anthropic-key", "Seal Anthropic Key"),
            ("OpenAI", "Used for GPT-5 & reasoning models", "OPENAI_API_KEY", "openai", "seal-openai-key", "Seal OpenAI Key"),
            ("Google Gemini", "Used for Gemini 2.5 Pro & Flash", "GOOGLE_API_KEY", "google", "seal-google-key", "Seal Google Key"),
        ];

        return new Surface(
            [
                new Heading("API Keys & Zero-Leakage Vault", Level: 3, Subtitle: "Keys are sealed in your brain's Key Vault and never shown again after saving."),
                .. vaults.Select(v => (Node)new SecretCardNode(v.Provider, v.Detail, SavedIn: $"Secure Vault · {v.SecretName}", Status: HasVaultEntry(v.SecretName, v.Connection) ? "saved" : "pending")),
                new Row(
                [
                    .. vaults.Select(v => ActionButton(v.ButtonId, v.ButtonLabel, AppSignals.Emit("settings", "set-variable", v.SecretName, $"vault://{v.SecretName}"), icon: "key-round")),
                    IntentButton("open-secure-vault-window", "Open Secure Vault Window", "os://window/secure", ButtonTone.Accent, "key-round"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "API keys");
    }

    private bool HasVaultEntry(string variableKey, string connectionKey)
        => (State.Variables ?? []).Any(v => string.Equals(v.Key, variableKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Value))
           || (State.ConnectionKeys ?? []).Any(k => string.Equals(k, connectionKey, StringComparison.OrdinalIgnoreCase));

    private Node RenderVariablesSection()
    {
        IReadOnlyList<string>[] rows =
        [
            ["DEFAULT_MODEL", State.DefaultModel, "brain"],
            ["STYLE_PRESET", State.StylePreset, "local"],
            ["TIME_ZONE", State.TimeZone, "local"],
            .. (State.Variables ?? []).Select(static variable => (IReadOnlyList<string>)[variable.Key, variable.Value, "brain"]),
        ];

        return new Surface(
            [
                new Heading("Environment Variables & Scoped Secrets", Level: 3, Subtitle: "Key-value configuration scoped across local, brain, and system."),
                new DataTable("Variables & Secrets", ["Key", "Value", "Scope"], rows, SourceNeuronUri: "neuron://local/os.settings/settings"),
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Variables and secrets");
    }

    private Node RenderBillingSection() => new Surface(
        [
            new Heading("Billing & Compute Credits", Level: 3, Subtitle: "Real-time token consumption, credit balance, and spend limits."),
            new Grid(
                3,
                [
                    Ui.StatCard("bill-credits", "Metered Spend", _computeSpendUsd, _computeRowsSummary, _computeBudgetExceeded ? BadgeTone.Error : BadgeTone.Done, "wallet"),
                    Ui.StatCard("bill-tokens", "Default Model", State.DefaultModel, "Active routing", BadgeTone.Accent, "cpu"),
                    Ui.StatCard("bill-limit", "Monthly Cap", _computeBudgetUsd, _computeBudgetExceeded ? "Cap Exceeded" : "Within Cap", _computeBudgetExceeded ? BadgeTone.Error : BadgeTone.Neutral, "gauge"),
                ],
                Gap: 12),
            new Row(
            [
                ActionButton("billing-cap-10", "Set $10 Cap", AppSignals.Emit("settings", "set-budget", "10000000"), icon: "gauge"),
                ActionButton("billing-cap-50", "Set $50 Cap", AppSignals.Emit("settings", "set-budget", "50000000"), icon: "gauge"),
                ActionButton("billing-cap-200", "Set $200 Cap", AppSignals.Emit("settings", "set-budget", "200000000"), icon: "gauge"),
            ]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Billing");

    private Node RenderTeamSection(string? subPage)
    {
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return DetailSubPage(
                $"Team / {subPage}",
                $"PersonPage · {subPage}",
                "Individual circle role and capability grant overrides.",
                new RecordCardNode(
                    "Member Grants",
                    subPage,
                    [
                        new RecordFieldNode("Role", "Owner · Full Brain Administration"),
                        new RecordFieldNode("Write Policy", "LFP ApprovalCard Gate"),
                    ],
                    Subtitle: $"Member · {subPage}"),
                "back-to-team",
                "Back to Team");
        }

        IReadOnlyList<string>[] memberRows =
        [
            [State.DisplayName, "Owner", "Active"],
            .. (State.TeamFriends ?? []).Select(static friend =>
            {
                var label = string.IsNullOrWhiteSpace(friend.DisplayName) ? $"@{friend.Handle}" : $"{friend.DisplayName} (@{friend.Handle})";
                return (IReadOnlyList<string>)[label, friend.Scope, "Verified"];
            }),
        ];

        return new Surface(
            [
                new Heading("Team Roster & LFP Permissions", Level: 3, Subtitle: "Manage circle members, roles (Owner, Member, Guest), and capability grants."),
                new DataTable("Brain Members", ["Member", "Role", "2FA"], memberRows, SourceNeuronUri: "neuron://local/identity/directory"),
                new Row(
                [
                    ActionButton("inspect-owner-grants", "Inspect Owner Grants", AppSignals.Emit("settings", "navigate", "team", "owner"), ButtonTone.Accent, "shield-check"),
                    ActionButton("invite-teammate", "Invite Teammate", AppSignals.Emit("settings", "navigate", "team", "", "invite"), icon: "user-plus"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Team and permissions");
    }

    private Node RenderNotificationsSection()
    {
        var approvalCards = _pendingApprovals.Select(static approval => (Node)new ApprovalCardNode(
            $"settings-approval-{approval.PlanId}",
            approval.PlanId,
            approval.Title,
            approval.Detail,
            [new ApprovalRowNode("Subject", "", approval.Subject)],
            SystemName: "Ino",
            Status: "pending",
            Resolve: new Resolve(approval.PlanId)));

        return new Surface(
            [
                new Heading("Notifications & Quiet Hours", Level: 3, Subtitle: "Choose which neural events alert you."),
                new Switch("needs-you-lfp-approvalcards-waiting-for", "Needs you · LFP ApprovalCards waiting for confirmation", true),
                new Switch("background-jobs-long-running-agent-compl", "Background jobs & long-running agent completions", true),
                new Switch("connection-expirations-health-probe-fail", "Connection expirations & health probe failures", true),
                new Switch("quiet-hours-schedule-22-00-07-00", "Quiet hours schedule (22:00 – 07:00)", false),
                .. approvalCards,
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Notifications");
    }

    private Node RenderKeyboardSection() => new Surface(
        [
            new Heading("Keyboard Shortcuts", Level: 3, Subtitle: "Global hotkeys across Desktop, Conversation, and Editor."),
            new DataTable(
                "Keyboard Shortcuts",
                ["Scope", "Action", "Shortcut"],
                [
                    ["Desktop", "Open Command Palette", "Ctrl+K / ⌘K"],
                    ["Desktop", "Switch MRU Windows", "Alt+` / ⌥`"],
                    ["Settings", "Dismiss Modal Sheet / Clear Search", "Esc"],
                    ["Conversation", "New Ino Session", "Ctrl+N / ⌘N"],
                ],
                SourceNeuronUri: "neuron://local/apps/settings#keyboard"),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Keyboard");

    private Node RenderDangerSection() => new Surface(
        [
            new Heading("Danger Zone", Level: 3, Subtitle: "Portable brain archive export/restore and irreversible account actions."),
            new Row(
            [
                ActionButton("export-brain-archive-zip", "Export Brain Archive", AppSignals.Emit("settings", "export-brain"), ButtonTone.Accent, "download"),
                ActionButton("import-brain-archive", "Restore Brain Archive", AppSignals.Emit("settings", "import-brain"), icon: "upload"),
                new Button("delete-account-reset-brain", "Delete Account / Reset Brain", Emit: AppSignals.Emit("settings", "navigate", "danger", "", "confirm-delete"), Tone: "danger") { Small = true, Icon = "trash-2", Style = "danger" },
            ]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Danger zone");

    private static Node DetailSubPage(string surfaceTitle, string heading, string subtitle, RecordCardNode card, string backId, string backLabel)
        => new Surface(
            [
                new Heading(heading, Level: 3, Subtitle: subtitle),
                card,
                ActionButton(backId, backLabel, AppSignals.Emit("settings", "go-back"), icon: "arrow-left"),
            ],
            Kind: SurfaceKind.Elevated,
            Title: surfaceTitle);

    private static Node ActionButton(string id, string label, Emit emit, string tone = ButtonTone.Neutral, string? icon = null)
        => new Button(id, label, Emit: emit, Tone: tone) { Small = true, Icon = icon };

    private static Node IntentButton(string id, string label, string uri, string tone = ButtonTone.Neutral, string? icon = null)
        => new Button(id, label, Intent: AppSignals.Open(uri), Tone: tone) { Small = true, Icon = icon };

    private Node PrefButton(
        string id,
        string label,
        bool selected,
        string? theme = null,
        string? model = null,
        string? preset = null,
        string? barLayout = null,
        string? themeMode = null,
        string? desktopTone = null,
        string? icon = null)
    {
        var args = new List<string>
        {
            State.DisplayName,
            theme ?? State.Theme,
            model ?? State.DefaultModel,
        };
        if (preset != null || barLayout != null || themeMode != null || desktopTone != null)
        {
            args.Add(preset ?? State.StylePreset);
            args.Add(barLayout ?? State.BarLayout);
        }
        if (themeMode != null || desktopTone != null)
        {
            args.Add(themeMode ?? State.ThemeMode);
        }
        if (desktopTone != null)
        {
            args.Add(desktopTone);
        }

        return new Button(id, label, Emit: AppSignals.Emit("settings", "save-preferences", [.. args]), Tone: selected ? ButtonTone.Accent : ButtonTone.Neutral)
        {
            Small = true,
            Icon = icon,
        };
    }

    private static void Swallow(Exception error, string reason)
    {
        _ = error;
        _ = reason;
    }

    private async Task UpdateAsync(SettingsAppState next)
    {
        try
        {
            await Show(next);
        }
        catch (InvalidOperationException)
        {
            typeof(DigitalBrain.Neurons.Neuron<SettingsAppState>).GetProperty("State", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .SetValue(this, next);
        }
    }
}
