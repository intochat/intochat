using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.Settings;

public sealed record SettingsViewContext(
    string ComputeSpendUsd,
    string ComputeBudgetUsd,
    bool ComputeBudgetExceeded,
    string ComputeRowsSummary,
    IReadOnlyList<Approval> PendingApprovals,
    IReadOnlyDictionary<string, bool>? SecretStatuses = null);

internal static class SettingsViewRenderer
{
    public static View Render(SettingsAppState state, SettingsViewContext context)
    {
        var section = SettingsDecisions.NormalizeSection(state.Section);
        var sectionEntry = SettingsApp.SearchIndex.First(entry => entry.SectionId == section);

        var nodes = new List<Node>
        {
            RenderHeader(state, sectionEntry)
        };
        if (state.OverrideBody != null)
        {
            nodes.Add(state.OverrideBody);
            return new(state.Title, new Column([.. nodes]) { Gap = 14 });
        }
        if (!string.IsNullOrEmpty(state.Sheet))
        {
            nodes.Add(RenderModalSheet(state, state.Sheet!));
        }
        nodes.Add(RenderSectionBody(state, section, state.SubPage, context));
        return new(state.Title, new Column([.. nodes]) { Gap = 14 });
    }

    private static Node RenderHeader(SettingsAppState state, SettingsSearchEntry sectionEntry)
    {
        var canGoBack = !string.IsNullOrEmpty(state.SubPage) || (state.History is { Count: > 0 });
        var breadcrumb = string.IsNullOrEmpty(state.SubPage) ? sectionEntry.Title : $"{sectionEntry.Title} / {state.SubPage}";
        var actions = new List<Node>();
        if (canGoBack)
        {
            actions.Add(ActionButton("back", "Back", AppSignals.Emit("settings", "go-back"), icon: "arrow-left"));
        }
        actions.Add(new TagPill(sectionEntry.Group, "neutral", sectionEntry.Icon));
        actions.Add(new TagPill($"{state.StylePreset} · {state.ThemeMode}", "accent", "palette"));
        actions.Add(new Text(state.Status) { Tone = "muted", Tabular = true });

        return new Surface([new Heading(breadcrumb, Level: 2, Subtitle: sectionEntry.Keywords), new Row([.. actions]) { Gap = 8 }], Kind: SurfaceKind.Acrylic, Title: breadcrumb);
    }

    private static Node RenderModalSheet(SettingsAppState state, string sheet) => new Surface(
        [
            new Heading($"Sheet · {sheet}", Level: 3, Subtitle: "Modal configuration overlay (press Esc or Close to dismiss)."),
            new Text(sheet switch
            {
                "install-link" => "Paste a verified Smart App link (https://intochat.app/a/...) to inspect its manifest and capability requirements.",
                "add-connection" or "google-drive" => "Authorize an OAuth 2.0 or token-based integration through the zero-leakage Capability Broker.",
                "ollama-config" => "Configure local Ollama / LM Studio server endpoint (http://localhost:11434) and reasoning effort.",
                "invite" => "Invite a teammate to this brain and assign Owner, Member, or Guest circle permissions.",
                "export-archive" => state.ExportedArchiveJson ?? "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units.",
                "confirm-delete" => "Two-step confirmation required: deleting or resetting this brain permanently revokes all sessions.",
                _ => $"Active modal sheet: {sheet}",
            }),
            new Row([ActionButton("close-sheet", "Close Sheet", AppSignals.Emit("settings", "navigate", state.Section, state.SubPage ?? "", ""), icon: "x")]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: $"Modal Sheet · {sheet}");

    private static Node RenderSectionBody(SettingsAppState state, string section, string? subPage, SettingsViewContext context) => section switch
    {
        "desktop" => RenderDesktopSection(state),
        "apps" => RenderAppsSection(state, subPage),
        "connections" => RenderConnectionsSection(state, subPage),
        "files" => RenderFilesSection(state),
        "models" => RenderModelsSection(state, subPage),
        "keys" => RenderKeysSection(state, context),
        "variables" => RenderVariablesSection(state),
        "billing" => RenderBillingSection(state, context),
        "team" => RenderTeamSection(state, subPage),
        "notifications" => RenderNotificationsSection(context),
        "keyboard" => RenderKeyboardSection(),
        "danger" => RenderDangerSection(),
        _ => RenderGeneralSection(state),
    };

    private static Node RenderGeneralSection(SettingsAppState state) => new Column(
    [
        new Grid(
            3,
            [
                UI.StatCard("stat-preset", "Active UI Identity", state.StylePreset, state.BarLayout, BadgeTone.Accent, "palette"),
                UI.StatCard("stat-model", "Default Brain Model", state.DefaultModel, "Local + Cloud", BadgeTone.Done, "cpu"),
                UI.StatCard("stat-vault", "Zero-Leakage Vault", $"{(state.ConnectionKeys ?? []).Count} Connections", "Active", BadgeTone.Neutral, "key-round"),
            ],
            Gap: 12),
        new Surface(
            [
                new Heading("Appearance & Theme Mode", Level: 3, Subtitle: "How IntoChat looks and reads for you."),
                new Row(
                [
                    PrefButton(state, "light", "Light", state.ThemeMode == "light", theme: "light", themeMode: "light", icon: "sun"),
                    PrefButton(state, "dark", "Dark", state.ThemeMode == "dark", theme: "dark", themeMode: "dark", icon: "moon"),
                    PrefButton(state, "auto", "Auto", state.ThemeMode == "auto", themeMode: "auto", icon: "sparkles"),
                ]) { Gap = 8 },
                new Heading("Theme & UI Identity Presets", Level: 3, Subtitle: "Switch live Acrylic glass presets, colour themes, and adaptive OS bar layouts."),
                new Row(
                    new (string Id, string Label)[]
                    {
                        ("fluent-acrylic", "Fluent Acrylic"), ("spatial-glass", "Spatial Glass"), ("cyber-synaptic", "Cyber Synaptic"),
                        ("warm-paper", "Warm Paper"), ("midnight", "Midnight"), ("nordic", "Nordic"),
                    }.Select(p => PrefButton(state, p.Id, p.Label, state.StylePreset == p.Id, preset: p.Id)).ToArray()) { Gap = 8 },
                new RecordCardNode(
                    "Locale & Regional Formatting",
                    state.Language,
                    [
                        new RecordFieldNode("Language", state.Language),
                        new RecordFieldNode("Time Zone", state.TimeZone),
                        new RecordFieldNode("Date Format", state.DateFormat),
                        new RecordFieldNode("Desktop Tone", state.DesktopTone),
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

    private static Node RenderDesktopSection(SettingsAppState state) => new Surface(
        [
            new Heading("Desktop Background Tone", Level: 3, Subtitle: "Select the ambient desktop canvas tone."),
            new Row(
                new (string Id, string Label)[] { ("cream", "Cream"), ("sand", "Sand"), ("peat", "Peat"), ("ink", "Ink") }
                    .Select(t => PrefButton(state, t.Id, t.Label, state.DesktopTone == t.Id, themeMode: state.ThemeMode, desktopTone: t.Id)).ToArray()) { Gap = 8 },
            new Heading("OS Bar Layout & Widgets", Level: 3, Subtitle: "Configure Dock pinned apps and status bar widgets."),
            new Row(
                new (string Id, string Label)[] { ("floating-glass", "Floating Glass"), ("compact-pill", "Compact Pill"), ("classic-bottom", "Classic Bottom") }
                    .Select(b => PrefButton(state, b.Id, b.Label, state.BarLayout == b.Id, barLayout: b.Id)).ToArray()) { Gap = 8 },
            new Switch("show-notifications-badge-in-os-bar", "Show Notifications badge in OS Bar", true),
            new Switch("show-live-token-compute-meter-in-os-bar", "Show live Token & Compute meter in OS Bar", true),
            new Switch("show-clock-schedule-calendar-in-os-bar", "Show Clock & Schedule calendar in OS Bar", true),
        ],
        Kind: SurfaceKind.Elevated,
        Title: "Desktop & Dock");

    private static Node RenderAppsSection(SettingsAppState state, string? subPage)
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

        var unitButtons = (state.DeployedUnits ?? [])
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

    private static Node RenderConnectionsSection(SettingsAppState state, string? subPage)
    {
        var keys = state.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
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

    private static Node RenderFilesSection(SettingsAppState state)
    {
        var keys = state.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
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

    private static Node RenderModelsSection(SettingsAppState state, string? subPage)
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
                        new RecordFieldNode("Active Local Model", state.DefaultModel),
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
                    PrefButton(state, "qwen3-5-4b-local", "qwen3.5:4b (Local)", state.DefaultModel == "qwen3.5:4b", model: "qwen3.5:4b"),
                    PrefButton(state, "claude-sonnet-4-5", "claude-sonnet-4-5", state.DefaultModel == "claude-sonnet-4-5", model: "claude-sonnet-4-5"),
                    PrefButton(state, "gpt-5", "gpt-5", state.DefaultModel == "gpt-5", model: "gpt-5"),
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

    private static Node RenderKeysSection(SettingsAppState state, SettingsViewContext context)
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
                .. vaults.Select(v =>
                {
                    var isConfigured = (context.SecretStatuses != null && context.SecretStatuses.TryGetValue(v.SecretName, out var configured) && configured)
                        || HasVaultEntry(state, v.SecretName, v.Connection);
                    return (Node)new SecretCardNode(
                        v.Provider,
                        $"{v.Detail} · {(isConfigured ? "Configured" : "Not set")}",
                        SavedIn: $"Secure Vault · {v.SecretName}",
                        Status: isConfigured ? "saved" : "pending");
                }),
                new Row(
                [
                    .. vaults.Select(v => ActionButton(v.ButtonId, v.ButtonLabel, AppSignals.Emit("settings", "set-variable", v.SecretName, $"vault://{v.SecretName}"), icon: "key-round")),
                    IntentButton("open-secure-vault-window", "Open Secure Vault Window", "os://window/secure", ButtonTone.Accent, "key-round"),
                ]) { Gap = 8 },
            ],
            Kind: SurfaceKind.Elevated,
            Title: "API keys");
    }

    private static bool HasVaultEntry(SettingsAppState state, string variableKey, string connectionKey)
        => (state.Variables ?? []).Any(v => string.Equals(v.Key, variableKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Value))
           || (state.ConnectionKeys ?? []).Any(k => string.Equals(k, connectionKey, StringComparison.OrdinalIgnoreCase));

    private static Node RenderVariablesSection(SettingsAppState state)
    {
        IReadOnlyList<string>[] rows =
        [
            ["DEFAULT_MODEL", state.DefaultModel, "brain"],
            ["STYLE_PRESET", state.StylePreset, "local"],
            ["TIME_ZONE", state.TimeZone, "local"],
            .. (state.Variables ?? []).Select(static variable => (IReadOnlyList<string>)[variable.Key, SettingsDecisions.RedactSecretDisplay(variable.Key, variable.Value), "brain"]),
        ];

        return new Surface(
            [
                new Heading("Environment Variables & Scoped Secrets", Level: 3, Subtitle: "Key-value configuration scoped across local, brain, and system."),
                new DataTable("Variables & Secrets", ["Key", "Value", "Scope"], rows, SourceNeuronUri: "neuron://local/os.settings/settings"),
            ],
            Kind: SurfaceKind.Elevated,
            Title: "Variables and secrets");
    }

    private static Node RenderBillingSection(SettingsAppState state, SettingsViewContext context) => new Surface(
        [
            new Heading("Billing & Compute Credits", Level: 3, Subtitle: "Real-time token consumption, credit balance, and spend limits."),
            new Grid(
                3,
                [
                    UI.StatCard("bill-credits", "Metered Spend", context.ComputeSpendUsd, context.ComputeRowsSummary, context.ComputeBudgetExceeded ? BadgeTone.Error : BadgeTone.Done, "wallet"),
                    UI.StatCard("bill-tokens", "Default Model", state.DefaultModel, "Active routing", BadgeTone.Accent, "cpu"),
                    UI.StatCard("bill-limit", "Monthly Cap", context.ComputeBudgetUsd, context.ComputeBudgetExceeded ? "Cap Exceeded" : "Within Cap", context.ComputeBudgetExceeded ? BadgeTone.Error : BadgeTone.Neutral, "gauge"),
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

    private static Node RenderTeamSection(SettingsAppState state, string? subPage)
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
            [state.DisplayName, "Owner", "Active"],
            .. (state.TeamFriends ?? []).Select(static friend =>
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

    private static Node RenderNotificationsSection(SettingsViewContext context)
    {
        var approvalCards = context.PendingApprovals.Select(static approval => (Node)new ApprovalCardNode(
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

    private static Node RenderKeyboardSection() => new Surface(
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

    private static Node RenderDangerSection() => new Surface(
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

    private static Node PrefButton(
        SettingsAppState state,
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
            state.DisplayName,
            theme ?? state.Theme,
            model ?? state.DefaultModel,
        };
        if (preset != null || barLayout != null || themeMode != null || desktopTone != null)
        {
            args.Add(preset ?? state.StylePreset);
            args.Add(barLayout ?? state.BarLayout);
        }
        if (themeMode != null || desktopTone != null)
        {
            args.Add(themeMode ?? state.ThemeMode);
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
}
