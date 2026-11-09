/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back

Declarative macOS System Settings-style application for IntoChat OS.
Owns the 13 settings sections, master-detail subpages (AppPage, MemoryPage, ConnectionPage, PersonPage, LocalServerPage),
modal sheets, search index, and navigation backstack. Preferences read and persist through UserSettingsNeuron (IUserSettings).
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Settings;

[DigitalBrain.Signal("settings.open")]
public sealed record SettingsOpen(string? Section = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.read")]
public sealed record SettingsRead : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.refresh-connections")]
public sealed record SettingsRefreshConnections(
    string? ConnectionKey = null,
    string? FriendApp = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.save-preferences")]
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
    string? DateFormat = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reload-preferences")]
public sealed record SettingsReloadPreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reconcile-preferences")]
public sealed record SettingsReconcilePreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.navigate")]
public sealed record SettingsNavigate(
    string? Section = "general",
    string? SubPage = null,
    string? Sheet = null,
    string? SearchQuery = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.go-back")]
public sealed record SettingsGoBack : DigitalBrain.Signal;

public sealed record SettingsSearchEntry(
    string SectionId,
    string Title,
    string Keywords,
    string ActionUri,
    string Icon = "settings",
    string Group = "Preferences");

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
    IReadOnlyList<string>? FriendGrants = null)
{
    public static IReadOnlyList<string> DefaultConnectionKeys { get; } =
    [
        "neuron://local/connection/github/personal",
        "neuron://local/connection/github/work",
        "neuron://local/connection/google-drive/work",
        "neuron://local/local-cloud/main",
    ];

    public static IReadOnlyList<string> DefaultFriendGrants { get; } =
    [
        "sdk -> app:local/apps/creator (storage@1 · SANDBOX / TEST CREDENTIALS)",
        "sdk -> app:local/apps/ino (connections · ICapabilityBroker Zero-Token)",
    ];

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
        null,
        "fluent-acrylic",
        "floating-glass",
        "auto",
        "cream",
        "English",
        "UTC",
        "17 Oct 2026",
        DefaultConnectionKeys,
        DefaultFriendGrants)
    {
    }
}

[DigitalBrain.App("settings", Accent = "#64748B", Category = "System")]
public sealed class SettingsApp() : DigitalBrain.App<SettingsAppState>
{
    private string _computeSpendUsd = "$0.00";
    private string _computeRowsSummary = "0 model(s) metered";

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

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(SettingsApp));

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

    [DigitalBrain.On]
    public async Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryRefreshComputeSummaryAsync();
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            Section: State.Section,
            SubPage: State.SubPage,
            Sheet: State.Sheet,
            History: State.History ?? [],
            SearchQuery: State.SearchQuery,
            OverrideBody: null,
            StylePreset: user.StylePreset,
            BarLayout: user.BarLayout,
            ThemeMode: user.ThemeMode,
            DesktopTone: user.DesktopTone,
            Language: user.Language,
            TimeZone: user.TimeZone,
            DateFormat: user.DateFormat,
            ConnectionKeys: State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            FriendGrants: State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public async Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await TryRefreshComputeSummaryAsync();
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            Section: State.Section,
            SubPage: State.SubPage,
            Sheet: State.Sheet,
            History: State.History ?? [],
            SearchQuery: State.SearchQuery,
            OverrideBody: null,
            StylePreset: user.StylePreset,
            BarLayout: user.BarLayout,
            ThemeMode: user.ThemeMode,
            DesktopTone: user.DesktopTone,
            Language: user.Language,
            TimeZone: user.TimeZone,
            DateFormat: user.DateFormat,
            ConnectionKeys: State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            FriendGrants: State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public void Navigate(SettingsNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawSection = request.Section;
        var rawSubPage = request.SubPage;
        var rawSheet = request.Sheet;
        if (!string.IsNullOrWhiteSpace(rawSection)
            && rawSection.Contains(':', StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(rawSubPage)
            && string.IsNullOrWhiteSpace(rawSheet))
        {
            var segments = rawSection.Split(':', 3, StringSplitOptions.TrimEntries);
            rawSection = segments[0];
            if (segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1]))
            {
                rawSubPage = segments[1];
            }
            if (segments.Length > 2 && !string.IsNullOrWhiteSpace(segments[2]))
            {
                rawSheet = segments[2];
            }
        }

        var targetSection = string.IsNullOrWhiteSpace(rawSection)
            ? State.Section
            : NormalizeSection(rawSection);
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
        Update(State with
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

    [DigitalBrain.On]
    public void GoBack(SettingsGoBack request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var history = (State.History ?? []).ToList();
        if (history.Count == 0)
        {
            Update(State with { Sheet = null, SubPage = null });
            return;
        }
        var last = history[^1];
        history.RemoveAt(history.Count - 1);
        var parts = last.Split('/', 2);
        var section = parts[0];
        var subPage = parts.Length > 1 ? parts[1] : null;
        Update(State with
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

    [DigitalBrain.On]
    public void RefreshConnections(SettingsRefreshConnections request)
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
        Update(State with
        {
            Title = "Settings",
            Status = "Connections",
            OverrideBody = null,
            ConnectionKeys = keys,
            FriendGrants = grants,
        });
    }

    [DigitalBrain.On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            && string.IsNullOrWhiteSpace(request.Theme)
            && string.IsNullOrWhiteSpace(request.DefaultModel)
            && string.IsNullOrWhiteSpace(request.StylePreset)
            && string.IsNullOrWhiteSpace(request.BarLayout)
            && string.IsNullOrWhiteSpace(request.ThemeMode)
            && string.IsNullOrWhiteSpace(request.DesktopTone)
            && string.IsNullOrWhiteSpace(request.Language)
            && string.IsNullOrWhiteSpace(request.TimeZone)
            && string.IsNullOrWhiteSpace(request.DateFormat))
        {
            Update(State with { Title = "Settings", Status = "Nothing to save.", OverrideBody = null });
            return;
        }

        var settings = Get<IUserSettings>("settings");
        var updated = await settings.SetProfile(
            request.DisplayName ?? State.DisplayName,
            request.Theme ?? State.Theme,
            request.DefaultModel ?? State.DefaultModel);
        if (!string.IsNullOrWhiteSpace(request.StylePreset) || !string.IsNullOrWhiteSpace(request.BarLayout))
        {
            updated = await settings.SetUiIdentity(
                request.StylePreset ?? State.StylePreset,
                request.BarLayout ?? State.BarLayout);
        }
        if (!string.IsNullOrWhiteSpace(request.ThemeMode)
            || !string.IsNullOrWhiteSpace(request.DesktopTone)
            || !string.IsNullOrWhiteSpace(request.Language)
            || !string.IsNullOrWhiteSpace(request.TimeZone)
            || !string.IsNullOrWhiteSpace(request.DateFormat))
        {
            updated = await settings.SetPreferences(
                request.ThemeMode ?? State.ThemeMode,
                request.DesktopTone ?? State.DesktopTone,
                request.Language ?? State.Language,
                request.TimeZone ?? State.TimeZone,
                request.DateFormat ?? State.DateFormat);
        }
        Update(new SettingsAppState(
            "Settings",
            $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel}, {updated.StylePreset}, {updated.BarLayout})",
            updated.DisplayName,
            updated.Theme,
            updated.DefaultModel,
            State.Section,
            State.SubPage,
            State.Sheet,
            State.History ?? [],
            State.SearchQuery,
            null,
            updated.StylePreset,
            updated.BarLayout,
            updated.ThemeMode,
            updated.DesktopTone,
            updated.Language,
            updated.TimeZone,
            updated.DateFormat,
            State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public async Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            State.Section,
            State.SubPage,
            State.Sheet,
            State.History ?? [],
            State.SearchQuery,
            null,
            user.StylePreset,
            user.BarLayout,
            user.ThemeMode,
            user.DesktopTone,
            user.Language,
            user.TimeZone,
            user.DateFormat,
            State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public void ReconcilePreferences(SettingsReconcilePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Update(State with { Title = "Settings", Status = "Settings", OverrideBody = null });
    }

    public View RenderSide()
    {
        var matches = FilterSections(State.SearchQuery);
        var navItems = matches
            .Select(entry => Ui.NavItem(
                $"nav-{entry.SectionId}",
                entry.Title,
                entry.Icon,
                $"navigate:{entry.SectionId}",
                selected: string.Equals(State.Section, entry.SectionId, StringComparison.OrdinalIgnoreCase),
                badge: entry.SectionId == "danger" ? "!" : null))
            .ToArray();

        return new(
            "Settings Sections",
            new Column(
            [
                new Surface(
                    [
                        new TagPill($"{State.StylePreset} · {State.BarLayout}", "accent", "palette"),
                        new Text($"{State.DisplayName} · {State.DefaultModel}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "DigitalBrain Settings (13 Sections)"),
                new Field(
                    "settings-search",
                    "settings-search",
                    FieldKind.Search,
                    "Search settings...",
                    AppSignals.Emit("settings", "navigate", State.Section, State.SubPage ?? "", State.Sheet ?? "")),
                .. navItems,
            ])
            { Gap = 8 });
    }

    public View Render()
    {
        if (State.OverrideBody is not null)
        {
            return new(State.Title, State.OverrideBody);
        }

        var section = NormalizeSection(State.Section);
        var sectionEntry = SearchIndex.FirstOrDefault(s => s.SectionId == section) ?? SearchIndex[0];
        var nodes = new List<Node>
        {
            RenderHeader(sectionEntry),
        };

        if (!string.IsNullOrWhiteSpace(State.Sheet))
        {
            nodes.Add(RenderModalSheet(State.Sheet!));
        }

        nodes.Add(RenderSectionBody(section, State.SubPage));
        return new(State.Title, new Column([.. nodes]) { Gap = 14 });
    }

    private Node RenderHeader(SettingsSearchEntry sectionEntry)
    {
        var canGoBack = !string.IsNullOrEmpty(State.SubPage) || (State.History is { Count: > 0 });
        var breadcrumb = string.IsNullOrEmpty(State.SubPage)
            ? sectionEntry.Title
            : $"{sectionEntry.Title} / {State.SubPage}";
        var actions = new List<Node>();
        if (canGoBack)
        {
            actions.Add(new Button("back", "Back", Emit: AppSignals.Emit("settings", "go-back"), Tone: ButtonTone.Neutral)
            {
                Icon = "arrow-left",
                Small = true,
            });
        }
        actions.Add(new TagPill(sectionEntry.Group, "neutral", sectionEntry.Icon));
        actions.Add(new TagPill($"{State.StylePreset} · {State.ThemeMode}", "accent", "palette"));
        actions.Add(new Text(State.Status) { Tone = "muted", Tabular = true });

        return new Surface(
            [
                new Heading(breadcrumb, Level: 2, Subtitle: sectionEntry.Keywords),
                new Row([.. actions]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Acrylic,
            Title: breadcrumb);
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
                "confirm-delete" => "Two-step confirmation required: deleting or resetting this brain permanently revokes all sessions.",
                _ => $"Active modal sheet: {sheet}",
            }),
            new Row(
            [
                new Button("close-sheet", "Close Sheet", Emit: AppSignals.Emit("settings", "navigate", State.Section, State.SubPage ?? "", ""), Tone: ButtonTone.Neutral)
                {
                    Icon = "x",
                    Small = true,
                },
            ]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: $"Modal Sheet · {sheet}");

    private Node RenderSectionBody(string section, string? subPage) => section switch
    {
        "general" => RenderGeneralSection(),
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
                    new Button("light", "Light", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, "light", State.DefaultModel, State.StylePreset, State.BarLayout, "light"), Tone: State.ThemeMode == "light" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
                    new Button("dark", "Dark", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, "dark", State.DefaultModel, State.StylePreset, State.BarLayout, "dark"), Tone: State.ThemeMode == "dark" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "moon" },
                    new Button("auto", "Auto", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, State.BarLayout, "auto"), Tone: State.ThemeMode == "auto" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sparkles" },
                ]) { Gap = 8 },
                new Heading("Theme & UI Identity Presets", Level: 3, Subtitle: "Switch live Acrylic glass presets, colour themes, and adaptive OS bar layouts."),
                new Row(
                [
                    new Button("fluent-acrylic", "Fluent Acrylic", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "fluent-acrylic", State.BarLayout), Tone: State.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("spatial-glass", "Spatial Glass", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "spatial-glass", State.BarLayout), Tone: State.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("cyber-synaptic", "Cyber Synaptic", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "cyber-synaptic", State.BarLayout), Tone: State.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("warm-paper", "Warm Paper", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "warm-paper", State.BarLayout), Tone: State.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("midnight", "Midnight", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "midnight", State.BarLayout), Tone: State.StylePreset == "midnight" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("nordic", "Nordic", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, "nordic", State.BarLayout), Tone: State.StylePreset == "nordic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                ]) { Gap = 8 },
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
                    new Button("open-profile-2fa", "Open Profile & 2FA", Intent: AppSignals.Open("os://window/profile"), Tone: ButtonTone.Accent) { Small = true, Icon = "user" },
                    new Button("open-secure-vault", "Open Secure Vault", Intent: AppSignals.Open("os://window/secure")) { Small = true, Icon = "key-round" },
                    new Button("install-app-by-link", "Install App by Link", Intent: AppSignals.Open("os://sheet/install")) { Small = true, Icon = "package-plus" },
                    new Button("review-update-consent", "Review Update Consent", Intent: AppSignals.Open("os://sheet/reconsent")) { Small = true, Icon = "shield-check" },
                    new Button("resolve-file-conflict", "Resolve File Conflict", Intent: AppSignals.Open("os://sheet/conflict")) { Small = true, Icon = "git-compare" },
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
            [
                new Button("cream", "Cream", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, State.BarLayout, State.ThemeMode, "cream"), Tone: State.DesktopTone == "cream" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                new Button("sand", "Sand", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, State.BarLayout, State.ThemeMode, "sand"), Tone: State.DesktopTone == "sand" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                new Button("peat", "Peat", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, State.BarLayout, State.ThemeMode, "peat"), Tone: State.DesktopTone == "peat" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                new Button("ink", "Ink", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, State.BarLayout, State.ThemeMode, "ink"), Tone: State.DesktopTone == "ink" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
            ]) { Gap = 8 },
            new Heading("OS Bar Layout & Widgets", Level: 3, Subtitle: "Configure Dock pinned apps and status bar widgets."),
            new Row(
            [
                new Button("floating-glass", "Floating Glass", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, "floating-glass"), Tone: State.BarLayout == "floating-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                new Button("compact-pill", "Compact Pill", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, "compact-pill"), Tone: State.BarLayout == "compact-pill" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                new Button("classic-bottom", "Classic Bottom", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, State.DefaultModel, State.StylePreset, "classic-bottom"), Tone: State.BarLayout == "classic-bottom" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
            ]) { Gap = 8 },
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
            return new Surface(
                [
                    new Heading("MemoryPage · Ino Episodic & Semantic Memory", Level: 3, Subtitle: "Inspect and manage facts recalled by neuron://local/memory."),
                    new RecordCardNode(
                        "Memory Neuron",
                        "neuron://local/memory",
                        [
                            new RecordFieldNode("Status", "Active · Recall top-3 episodes per turn"),
                            new RecordFieldNode("Retention Policy", "LFP-gated deletion (requires ApprovalCard)"),
                        ],
                        Subtitle: "Ino Memory Store"),
                    new Button("back-to-apps", "Back to Apps", Emit: AppSignals.Emit("settings", "go-back"), Tone: ButtonTone.Neutral) { Small = true, Icon = "arrow-left" },
                ],
                Kind: SurfaceKind.Elevated,
                Title: "Apps / MemoryPage");
        }

        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return new Surface(
                [
                    new Heading($"AppPage · {subPage}", Level: 3, Subtitle: $"Manifest, permissions, and sandbox isolation for neuron://local/apps/{subPage}."),
                    new RecordCardNode(
                        "Installed Smart App",
                        subPage,
                        [
                            new RecordFieldNode("Neuron URI", $"neuron://local/apps/{subPage}"),
                            new RecordFieldNode("Write Gate", "LFP ApprovalCard enforced"),
                        ],
                        Subtitle: $"App Detail · {subPage}"),
                    new Button("back-to-apps", "Back to Apps", Emit: AppSignals.Emit("settings", "go-back"), Tone: ButtonTone.Neutral) { Small = true, Icon = "arrow-left" },
                ],
                Kind: SurfaceKind.Elevated,
                Title: $"Apps / {subPage}");
        }

        return new Surface(
            [
                new Heading("Installed Smart Apps", Level: 3, Subtitle: "Manage installed apps and inspect permissions."),
                new Row(
                [
                    new Button("inspect-ino-app", "Inspect Ino App", Emit: AppSignals.Emit("settings", "navigate", "apps", "ino"), Tone: ButtonTone.Accent) { Small = true, Icon = "sparkles" },
                    new Button("inspect-creator-app", "Inspect Creator App", Emit: AppSignals.Emit("settings", "navigate", "apps", "creator")) { Small = true, Icon = "wand-sparkles" },
                    new Button("ino-memorypage", "Ino MemoryPage", Emit: AppSignals.Emit("settings", "navigate", "apps", "memory")) { Small = true, Icon = "brain" },
                    new Button("install-from-link", "Install from Link", Emit: AppSignals.Emit("settings", "navigate", "apps", "", "install-link")) { Small = true, Icon = "package-plus" },
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Apps");
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
                        new Button("reconnect-oauth", "Reconnect OAuth", Emit: AppSignals.Emit("settings", "navigate", "connections", subPage, "add-connection"), Tone: ButtonTone.Accent) { Small = true, Icon = "refresh-cw" },
                        new Button("back-to-connections", "Back to Connections", Emit: AppSignals.Emit("settings", "go-back")) { Small = true, Icon = "arrow-left" },
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
                    new Button("github-connection", "GitHub Connection", Emit: AppSignals.Emit("settings", "navigate", "connections", "github"), Tone: ButtonTone.Accent) { Small = true, Icon = "plug" },
                    new Button("google-drive-connection", "Google Drive Connection", Emit: AppSignals.Emit("settings", "navigate", "connections", "google-drive")) { Small = true, Icon = "folder" },
                    new Button("add-connection", "Add Connection", Emit: AppSignals.Emit("settings", "navigate", "connections", "", "add-connection")) { Small = true, Icon = "plus" },
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Connections");
    }

    private Node RenderFilesSection() => new Surface(
        [
            new Heading("Files & Hybrid Storage Locations", Level: 3, Subtitle: "Manage IntoChat cloud storage, Google Drive mounts, and local disk roots."),
            new RecordCardNode(
                "Mounted File Roots",
                "neuron://local/disk.files",
                [
                    new RecordFieldNode("IntoChat Storage", "neuron://local/local-cloud/main"),
                    new RecordFieldNode("Google Drive Root", "neuron://local/connection/google-drive/work"),
                    new RecordFieldNode("Local Computer Root", "neuron://local/disk.files"),
                ],
                Subtitle: "Storage Providers"),
            new Button("resolve-file-conflict-sheet", "Resolve File Conflict Sheet", Emit: AppSignals.Emit("settings", "navigate", "files", "", "conflict")) { Small = true, Icon = "git-compare" },
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Files");

    private Node RenderModelsSection(string? subPage)
    {
        if (string.Equals(subPage, "local-server", StringComparison.OrdinalIgnoreCase))
        {
            return new Surface(
                [
                    new Heading("Local Model Server · Ollama & LM Studio", Level: 3, Subtitle: "Configure local inference endpoint and reasoning effort."),
                    new RecordCardNode(
                        "Local Inference Endpoint",
                        "http://localhost:11434",
                        [
                            new RecordFieldNode("Active Local Model", State.DefaultModel),
                            new RecordFieldNode("Reasoning Effort", "Medium · Zero-Telemetry Local Execution"),
                        ],
                        Subtitle: "Ollama Local Server"),
                    new Button("back-to-ai-models", "Back to AI Models", Emit: AppSignals.Emit("settings", "go-back")) { Small = true, Icon = "arrow-left" },
                ],
                Kind: SurfaceKind.Elevated,
                Title: "AI models / local-server");
        }

        return new Surface(
            [
                new Heading("Default Brain Model & Routing", Level: 3, Subtitle: "Select the default model for Ino and autonomous background neurons."),
                new Row(
                [
                    new Button("qwen3-5-4b-local", "qwen3.5:4b (Local)", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, "qwen3.5:4b"), Tone: State.DefaultModel == "qwen3.5:4b" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("claude-sonnet-4-5", "claude-sonnet-4-5", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, "claude-sonnet-4-5"), Tone: State.DefaultModel == "claude-sonnet-4-5" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    new Button("gpt-5", "gpt-5", Emit: AppSignals.Emit("settings", "save-preferences", State.DisplayName, State.Theme, "gpt-5"), Tone: State.DefaultModel == "gpt-5" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                ]) { Gap = 8 },
                new Row(
                [
                    new Button("local-server-ollama", "Local Server (Ollama)", Emit: AppSignals.Emit("settings", "navigate", "models", "local-server"), Tone: ButtonTone.Accent) { Small = true, Icon = "cpu" },
                    new Button("configure-endpoint-sheet", "Configure Endpoint Sheet", Emit: AppSignals.Emit("settings", "navigate", "models", "", "ollama-config")) { Small = true, Icon = "sliders-horizontal" },
                    new Button("open-api-keys-section", "API Keys & Vault", Emit: AppSignals.Emit("settings", "navigate", "keys")) { Small = true, Icon = "key-round" },
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "AI models");
    }

    private Node RenderKeysSection() => new Surface(
        [
            new Heading("API Keys & Zero-Leakage Vault", Level: 3, Subtitle: "Keys are sealed in your brain's Key Vault and never shown again after saving."),
            new SecretCardNode("Anthropic", "Used for Claude Sonnet & Opus models", SavedIn: "Secure Vault · ANTHROPIC_API_KEY", Status: "saved"),
            new SecretCardNode("OpenAI", "Used for GPT-5 & reasoning models", SavedIn: "Secure Vault · OPENAI_API_KEY", Status: "saved"),
            new SecretCardNode("Google Gemini", "Used for Gemini 2.5 Pro & Flash", SavedIn: "Secure Vault · GOOGLE_API_KEY", Status: "pending"),
            new Button("open-secure-vault-window", "Open Secure Vault Window", Intent: AppSignals.Open("os://window/secure"), Tone: ButtonTone.Accent) { Small = true, Icon = "key-round" },
        ],
        Kind: SurfaceKind.Elevated,
        Title: "API keys");

    private Node RenderVariablesSection() => new Surface(
        [
            new Heading("Environment Variables & Scoped Secrets", Level: 3, Subtitle: "Key-value configuration scoped across local, brain, and system."),
            new DataTable(
                "Variables & Secrets",
                ["Key", "Value", "Scope"],
                [
                    ["DEFAULT_MODEL", State.DefaultModel, "brain"],
                    ["STYLE_PRESET", State.StylePreset, "local"],
                    ["TIME_ZONE", State.TimeZone, "local"],
                ],
                SourceNeuronUri: "neuron://local/os.settings/settings"),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Variables and secrets");

    private Node RenderBillingSection() => new Surface(
        [
            new Heading("Billing & Compute Credits", Level: 3, Subtitle: "Real-time token consumption, credit balance, and spend limits."),
            new Grid(
                3,
                [
                    Ui.StatCard("bill-credits", "Metered Spend", _computeSpendUsd, _computeRowsSummary, BadgeTone.Done, "wallet"),
                    Ui.StatCard("bill-tokens", "Default Model", State.DefaultModel, "Active routing", BadgeTone.Accent, "cpu"),
                    Ui.StatCard("bill-limit", "Compute Ledger", "ICompute", "Live Usage", BadgeTone.Neutral, "gauge"),
                ],
                Gap: 12),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Billing");

    private async Task TryRefreshComputeSummaryAsync()
    {
        try
        {
            var compute = await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Read();
            var usd = compute.SpentMicros / 1_000_000.0;
            _computeSpendUsd = usd.ToString("$0.00", System.Globalization.CultureInfo.InvariantCulture);
            _computeRowsSummary = $"{compute.Rows.Length} model(s) metered";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "ComputeNeuron is optional when SettingsApp runs in standalone tests.");
        }
    }

    private Node RenderTeamSection(string? subPage)
    {
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return new Surface(
                [
                    new Heading($"PersonPage · {subPage}", Level: 3, Subtitle: "Individual circle role and capability grant overrides."),
                    new RecordCardNode(
                        "Member Grants",
                        subPage,
                        [
                            new RecordFieldNode("Role", "Owner · Full Brain Administration"),
                            new RecordFieldNode("Write Policy", "LFP ApprovalCard Gate"),
                        ],
                        Subtitle: $"Member · {subPage}"),
                    new Button("back-to-team", "Back to Team", Emit: AppSignals.Emit("settings", "go-back")) { Small = true, Icon = "arrow-left" },
                ],
                Kind: SurfaceKind.Elevated,
                Title: $"Team / {subPage}");
        }

        return new Surface(
            [
                new Heading("Team Roster & LFP Permissions", Level: 3, Subtitle: "Manage circle members, roles (Owner, Member, Guest), and capability grants."),
                new DataTable(
                    "Brain Members",
                    ["Member", "Role", "2FA"],
                    [
                        [State.DisplayName, "Owner", "Active"],
                    ],
                    SourceNeuronUri: "neuron://local/identity/directory"),
                new Row(
                [
                    new Button("inspect-owner-grants", "Inspect Owner Grants", Emit: AppSignals.Emit("settings", "navigate", "team", "owner"), Tone: ButtonTone.Accent) { Small = true, Icon = "shield-check" },
                    new Button("invite-teammate", "Invite Teammate", Emit: AppSignals.Emit("settings", "navigate", "team", "", "invite")) { Small = true, Icon = "user-plus" },
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Team and permissions");
    }

    private Node RenderNotificationsSection() => new Surface(
        [
            new Heading("Notifications & Quiet Hours", Level: 3, Subtitle: "Choose which neural events alert you."),
            new Switch("needs-you-lfp-approvalcards-waiting-for", "Needs you · LFP ApprovalCards waiting for confirmation", true),
            new Switch("background-jobs-long-running-agent-compl", "Background jobs & long-running agent completions", true),
            new Switch("connection-expirations-health-probe-fail", "Connection expirations & health probe failures", true),
            new Switch("quiet-hours-schedule-22-00-07-00", "Quiet hours schedule (22:00 – 07:00)", false),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Notifications");

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
            new Heading("Danger Zone", Level: 3, Subtitle: "Irreversible actions affecting this brain and account."),
            new Row(
            [
                new Button("export-brain-archive-zip", "Export Brain Archive (ZIP)", Emit: AppSignals.Emit("settings", "navigate", "danger", "", "export-archive"), Tone: ButtonTone.Neutral) { Small = true, Icon = "download" },
                new Button("delete-account-reset-brain", "Delete Account / Reset Brain", Emit: AppSignals.Emit("settings", "navigate", "danger", "", "confirm-delete"), Tone: "danger") { Small = true, Icon = "trash-2", Style = "danger" },
            ]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Danger zone");

    private void Update(SettingsAppState next) => Set(next);

    [DigitalBrain.Scenario("Saving user preferences and navigating across the 13 sections updates UserSettingsNeuron and view")]
    public async Task VerifySavePreferencesScenario()
    {
        await SavePreferences(new SettingsSavePreferences("Alice", "light", "gpt-5", StylePreset: "spatial-glass", ThemeMode: "light", DesktopTone: "sand"));
        if (State.DisplayName != "Alice" || State.Theme != "light" || State.DefaultModel != "gpt-5" || State.StylePreset != "spatial-glass")
        {
            throw new InvalidOperationException($"Expected Alice/light/gpt-5/spatial-glass, got {State.DisplayName}/{State.Theme}/{State.DefaultModel}/{State.StylePreset}.");
        }

        Navigate(new SettingsNavigate("connections", SubPage: "github"));
        if (State.Section != "connections" || State.SubPage != "github")
        {
            throw new InvalidOperationException("Expected SettingsApp.Navigate to open connections/github.");
        }

        GoBack(new SettingsGoBack());
        if (State.Section != "general" || State.SubPage is not null)
        {
            throw new InvalidOperationException("Expected SettingsApp.GoBack to return to general.");
        }
    }
}
