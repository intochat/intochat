using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

internal static class SettingsViewRenderer
{
    public static View Render(SettingsAppState state)
    {
        using var scope = CommandBinding.PushOwner("app.settings");
        return new View(state.Title, BindOwner(RenderRoot(state, null), "app.settings"));
    }

    public static Node RenderRoot(SettingsAppState state, Rejected? lastRejection = null)
    {
        var section = SettingsDecisions.NormalizeSection(state.Section);
        var sectionEntry = SettingsApp.SearchIndex.First(entry => entry.SectionId == section);

        var sidebar = BuildSidebar(state, section);
        var contentChildren = new List<Node?>
        {
            RenderHeader(state, sectionEntry),
            lastRejection is not null
                ? Banner("Invalid setting value", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            !string.IsNullOrEmpty(state.Sheet)
                ? RenderModalSheet(state, state.Sheet!)
                : null,
            RenderSectionBody(state, section, state.SubPage),
        };

        return ResponsiveSplit(
            leading: sidebar,
            trailing: VStack(Space.Md, contentChildren.ToArray()),
            leadingExtent: 248);
    }

    private static Node BuildSidebar(SettingsAppState state, string activeSection)
    {
        var filtered = SettingsDecisions.FilterSections(state.SearchQuery);
        var groups = new[] { "Preferences", "Capabilities & Data", "Organization & System" };
        var nodes = new List<Node?>
        {
            SearchField("Search settings…", "Search settings")
                .Key("settings:search")
                .OnChange<SettingsAppState>(s => s.Filter(Arg.Text)),
            !string.IsNullOrWhiteSpace(state.SearchQuery)
                ? GhostButton("Clear search", "x")
                    .Key("settings:clear-search")
                    .Small()
                    .OnClick<SettingsAppState>(s => s.Filter(""))
                    .ToNode()
                : null,
        };

        if (filtered.Count == 0)
        {
            nodes.Add(Empty(
                "No matching sections",
                $"Nothing matched '{state.SearchQuery}'.",
                PrimaryButton("Show all sections")
                    .Key("settings:empty-reset")
                    .OnClick<SettingsAppState>(s => s.Filter(""))));
            return VStack(Space.Sm, nodes.ToArray());
        }

        foreach (var group in groups)
        {
            var entries = filtered
                .Where(e => string.Equals(e.Group, group, StringComparison.Ordinal) && e.SectionId != "danger")
                .ToArray();
            if (entries.Length == 0)
            {
                continue;
            }

            nodes.Add(Divider(group));
            nodes.Add(VStack(
                Space.Xxs,
                entries.Select(entry => (Node)NavItem(
                    $"nav-{entry.SectionId}",
                    entry.Title,
                    entry.Icon,
                    $"navigate:{entry.SectionId}",
                    selected: activeSection == entry.SectionId && string.IsNullOrEmpty(state.SubPage),
                    emit: AppSignals.Emit("settings", "navigate", entry.SectionId))).ToArray()));
        }

        if (filtered.FirstOrDefault(e => e.SectionId == "danger") is { } dangerEntry)
        {
            nodes.Add(Divider());
            nodes.Add(NavItem(
                "nav-danger",
                dangerEntry.Title,
                dangerEntry.Icon,
                "navigate:danger",
                selected: activeSection == "danger",
                emit: AppSignals.Emit("settings", "navigate", "danger")));
        }

        return VStack(Space.Sm, nodes.ToArray());
    }

    private static Node RenderHeader(SettingsAppState state, SettingsSearchEntry sectionEntry)
    {
        var breadcrumb = string.IsNullOrEmpty(state.SubPage)
            ? sectionEntry.Title
            : $"{sectionEntry.Title} / {state.SubPage}";

        var trailing = new List<Node?>
        {
            state.CanGoBack
                ? GhostButton("Back", "arrow-left")
                    .Key("settings:header-back")
                    .Small()
                    .Guard(state.CanReturnRule)
                    .Emits(AppSignals.Emit("settings", "go-back"))
                    .ToNode()
                : null,
            StatusPill(StatusKind.Idle, sectionEntry.Group),
            StatusPill(StatusKind.Done, $"{state.StylePreset} · {state.ThemeMode}"),
        };

        return VStack(Space.Xs)[
            HeaderBar(breadcrumb, state.Status, trailing.ToArray()),
            Divider()
        ];
    }

    private static Node RenderModalSheet(SettingsAppState state, string sheet)
    {
        var detail = sheet switch
        {
            "install-link" => "Paste a verified Smart App link (https://intochat.app/a/...) to inspect its manifest and capability requirements.",
            "add-connection" or "google-drive" or "google_drive" => "Authorize an OAuth 2.0 or token-based integration through the zero-leakage Capability Broker.",
            "ollama-config" => "Configure local Ollama or LM Studio server endpoint (http://localhost:11434) and reasoning effort.",
            "invite" => "Invite a teammate to this brain and assign Owner, Member, or Guest circle permissions.",
            "export-archive" => state.ExportedArchiveJson ?? "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units.",
            "confirm-delete" => "Two-step confirmation required: deleting or resetting this brain permanently revokes all sessions.",
            _ => $"Active modal sheet: {sheet}",
        };

        return Card($"Sheet · {sheet}", "Modal configuration sheet · Dismiss with Close or Esc")[
            VStack(Space.Sm)[
                Text(detail),
                HStack(Space.Sm)[
                    Button("Close sheet", "x")
                        .Key("settings:close-sheet")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", state.Section, state.SubPage ?? "", ""))
                ]
            ]
        ];
    }

    private static Node RenderSectionBody(SettingsAppState state, string section, string? subPage) => section switch
    {
        "desktop" => RenderDesktopSection(state),
        "apps" => RenderAppsSection(state, subPage),
        "connections" => RenderConnectionsSection(state, subPage),
        "files" => RenderFilesSection(state),
        "models" => RenderModelsSection(state, subPage),
        "keys" => RenderKeysSection(state),
        "variables" => RenderVariablesSection(state),
        "billing" => RenderBillingSection(state),
        "team" => RenderTeamSection(state, subPage),
        "notifications" => RenderNotificationsSection(state),
        "keyboard" => RenderKeyboardSection(),
        "danger" => RenderDangerSection(),
        _ => RenderGeneralSection(state),
    };

    private static Node RenderGeneralSection(SettingsAppState state)
    {
        var connectionCount = (state.ConnectionKeys ?? []).Count;

        return VStack(Space.Md)[
            Grid(3, Space.Md)[
                StatCard("stat-preset", "Active UI identity", state.StylePreset, state.BarLayout, BadgeTone.Accent, "palette"),
                StatCard("stat-model", "Default brain model", state.DefaultModel, "Local + cloud", BadgeTone.Done, "cpu"),
                StatCard("stat-vault", "Connected integrations", $"{connectionCount}", connectionCount > 0 ? "Active" : "None yet", BadgeTone.Neutral, Icons.KeyRound)
            ],
            Surface(SurfaceKind.Elevated, "Appearance & Regional Preferences", "How IntoChat looks and formats dates for you")[
                VStack(Space.Md)[
                    SelectBox(
                        "Appearance mode",
                        state.ThemeMode,
                        Option("light", "Light")
                            .Key("opt-theme-light")
                            .Emits(PrefEmit(state, theme: "light", themeMode: "light")),
                        Option("dark", "Dark")
                            .Key("opt-theme-dark")
                            .Emits(PrefEmit(state, theme: "dark", themeMode: "dark")),
                        Option("auto", "Auto (follow system)")
                            .Key("opt-theme-auto")
                            .Emits(PrefEmit(state, themeMode: "auto"))),
                    HStack(Space.Sm)[
                        PrefButton(state, "pref-fluent-acrylic", "Fluent Acrylic", state.StylePreset == "fluent-acrylic", preset: "fluent-acrylic"),
                        PrefButton(state, "pref-spatial-glass", "Spatial Glass", state.StylePreset == "spatial-glass", preset: "spatial-glass"),
                        PrefButton(state, "pref-cyber-synaptic", "Cyber Synaptic", state.StylePreset == "cyber-synaptic", preset: "cyber-synaptic"),
                        PrefButton(state, "pref-warm-paper", "Warm Paper", state.StylePreset == "warm-paper", preset: "warm-paper"),
                        PrefButton(state, "pref-midnight", "Midnight", state.StylePreset == "midnight", preset: "midnight"),
                        PrefButton(state, "pref-nordic", "Nordic", state.StylePreset == "nordic", preset: "nordic")
                    ],
                    Table(
                        "Regional settings",
                        ["Setting", "Active value"],
                        [
                            ["Display name", state.DisplayName],
                            ["Language", state.Language],
                            ["Time zone", state.TimeZone],
                            ["Date format", state.DateFormat],
                            ["Desktop tone", state.DesktopTone],
                        ])
                ]
            ],
            Card("Profile, Secure Vault & OS Sheets", "Open focused security windows without leaving your current workspace")[
                HStack(Space.Sm)[
                    PrimaryButton("Open profile & 2FA", "user")
                        .Key("intent:profile")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/profile"),
                    Button("Open secure vault", Icons.KeyRound)
                        .Key("intent:vault")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/secure"),
                    Button("Install app by link", "package-plus")
                        .Key("intent:install")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://sheet/install"),
                    Button("Resolve file conflict", "git-compare")
                        .Key("intent:conflict")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://sheet/conflict")
                ]
            ]
        ];
    }

    private static Node RenderDesktopSection(SettingsAppState state) =>
        Surface(SurfaceKind.Elevated, "Desktop & Dock", "Ambient canvas tone, adaptive bar layout, and status indicators")[
            VStack(Space.Md)[
                Heading("Desktop canvas tone", 3, "Choose the warm paper or deep ink surface behind windows."),
                HStack(Space.Sm)[
                    PrefButton(state, "tone-cream", "Cream", state.DesktopTone == "cream", themeMode: state.ThemeMode, desktopTone: "cream"),
                    PrefButton(state, "tone-sand", "Sand", state.DesktopTone == "sand", themeMode: state.ThemeMode, desktopTone: "sand"),
                    PrefButton(state, "tone-peat", "Peat", state.DesktopTone == "peat", themeMode: state.ThemeMode, desktopTone: "peat"),
                    PrefButton(state, "tone-ink", "Ink", state.DesktopTone == "ink", themeMode: state.ThemeMode, desktopTone: "ink")
                ],
                SelectBox(
                    "OS bar layout",
                    state.BarLayout,
                    Option("floating-glass", "Floating Glass")
                        .Key("opt-bar-floating")
                        .Emits(PrefEmit(state, barLayout: "floating-glass")),
                    Option("compact-pill", "Compact Pill")
                        .Key("opt-bar-compact")
                        .Emits(PrefEmit(state, barLayout: "compact-pill")),
                    Option("classic-bottom", "Classic Bottom")
                        .Key("opt-bar-classic")
                        .Emits(PrefEmit(state, barLayout: "classic-bottom"))),
                Divider("OS bar indicators"),
                ToggleSwitch("Show notifications badge in OS bar", state.ShowOsBarNotifications)
                    .Key("toggle:bar-notifications")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("bar-notifications")),
                ToggleSwitch("Show live token & compute meter in OS bar", state.ShowOsBarComputeMeter)
                    .Key("toggle:bar-compute")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("bar-compute")),
                ToggleSwitch("Show clock & schedule calendar in OS bar", state.ShowOsBarClock)
                    .Key("toggle:bar-clock")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("bar-clock"))
            ]
        ];

    private static Node RenderAppsSection(SettingsAppState state, string? subPage)
    {
        if (string.Equals(subPage, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return DetailSubPage(
                "Apps / MemoryPage",
                "MemoryPage · Ino Episodic & Semantic Memory",
                "Ino Memory Store · Inspect and manage facts recalled by neuron://local/memory.",
                [
                    ["Neuron URI", "neuron://local/memory"],
                    ["Store", "Ino Memory Store"],
                    ["Status", "Active · Recall top-3 episodes per turn"],
                    ["Retention policy", "Gate 2.0 ApprovalCard required for deletion"],
                ],
                "back-to-apps",
                "Back to Apps");
        }

        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return DetailSubPage(
                $"Apps / {subPage}",
                $"AppPage · {subPage}",
                $"Manifest, permissions, and sandbox isolation for neuron://local/apps/{subPage}.",
                [
                    ["App key", subPage],
                    ["Neuron URI", $"neuron://local/apps/{subPage}"],
                    ["Write gate", "Gate 2.0 ApprovalCard enforced"],
                ],
                "back-to-apps",
                "Back to Apps");
        }

        var unitButtons = (state.DeployedUnits ?? [])
            .Select((alias, idx) => Button($"Unit · {alias}", "cpu")
                .Key($"inspect-unit-{idx}-{alias}")
                .Small()
                .Emits(AppSignals.Emit("settings", "navigate", "apps", alias))
                .ToNode())
            .ToArray();

        return Surface(SurfaceKind.Elevated, "Apps", "Installed Smart Apps, deployed Synapse units, and capability grants")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(StatusKind.Done, "Sandbox verified"),
                    StatusPill(StatusKind.Idle, $"{(state.DeployedUnits ?? []).Count} deployed unit(s)")
                ],
                HStack(Space.Sm)[
                    PrimaryButton("Inspect Ino app", Icons.Sparkles)
                        .Key("apps:inspect-ino")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "apps", "ino")),
                    Button("Inspect Creator app", "wand-sparkles")
                        .Key("apps:inspect-creator")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "apps", "creator")),
                    Button("Ino MemoryPage", "brain")
                        .Key("apps:inspect-memory")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "apps", "memory")),
                    Button("Install from link", "package-plus")
                        .Key("apps:install-link")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "apps", "", "install-link"))
                ],
                unitButtons.Length > 0
                    ? HStack(Space.Sm, unitButtons).ToNode()
                    : MutedText("No custom Synapse units deployed yet.").ToNode()
            ]
        ];
    }

    private static Node RenderConnectionsSection(SettingsAppState state, string? subPage)
    {
        var keys = state.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return Surface(SurfaceKind.Elevated, $"Connections / {subPage}", "Zero-token credential injection via ICapabilityBroker")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        Heading($"ConnectionPage · {subPage}", 3, "OAuth scopes, health probe, and capability bindings."),
                        StatusPill(StatusKind.Done, "Connected")
                    ],
                    Table(
                        $"Connection detail · {subPage}",
                        ["Field", "Value"],
                        [
                            ["Endpoint", subPage],
                            ["Neuron URI", $"neuron://local/connection/{subPage}"],
                            ["Broker mode", "Zero-token credential injection"],
                        ]),
                    HStack(Space.Sm)[
                        PrimaryButton("Reconnect OAuth", Icons.RefreshCw)
                            .Key("conn:reconnect-oauth")
                            .Small()
                            .Emits(AppSignals.Emit("settings", "navigate", "connections", subPage, "add-connection")),
                        Button("Back to Connections", "arrow-left")
                            .Key("conn:back")
                            .Small()
                            .Emits(AppSignals.Emit("settings", "go-back"))
                    ]
                ]
            ];
        }

        var connectionRows = keys.Count == 0
            ? [["Local storage", "Built-in", "Connected"]]
            : keys.Select((k, idx) => (IReadOnlyList<string>)[$"Connection {idx + 1}", k, "Connected"]).ToArray();

        return Surface(SurfaceKind.Elevated, "Connections", "Accounts and services this brain can reach without exposing raw secrets")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(keys.Count > 0 ? StatusKind.Done : StatusKind.Idle, $"{keys.Count} external connection(s)"),
                    StatusPill(StatusKind.Done, "Capability broker active")
                ],
                Table("Active connections", ["Slot", "Connection key", "Health"], connectionRows),
                HStack(Space.Sm)[
                    PrimaryButton("Add connection", Icons.Plus)
                        .Key("conn:add")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "connections", "", "add-connection")),
                    Button("GitHub connection", "plug")
                        .Key("conn:github")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "connections", "github")),
                    Button("Google Drive connection", Icons.Folder)
                        .Key("conn:google-drive")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "connections", "google-drive"))
                ]
            ]
        ];
    }

    private static Node RenderFilesSection(SettingsAppState state)
    {
        var keys = state.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
        IReadOnlyList<string>[] mountRows =
        [
            ["IntoChat Storage", "neuron://local/local-cloud/main", "Connected"],
            ["Local Computer Root", "neuron://local/disk.files", "Connected"],
            .. keys.Select(static (k, idx) => (IReadOnlyList<string>)[$"Connected Mount {idx + 1}", k, "Connected"]),
        ];

        return Surface(SurfaceKind.Elevated, "Files", "Manage IntoChat cloud storage, connected mounts, and local disk roots")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(StatusKind.Done, $"{mountRows.Length} mounted root(s)")
                ],
                Table("Mounted file roots", ["Mount", "Address", "Status"], mountRows),
                HStack(Space.Sm)[
                    Button("Resolve file conflict sheet", "git-compare")
                        .Key("files:conflict-sheet")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "files", "", "conflict"))
                ]
            ]
        ];
    }

    private static Node RenderModelsSection(SettingsAppState state, string? subPage)
    {
        if (string.Equals(subPage, "local-server", StringComparison.OrdinalIgnoreCase))
        {
            return DetailSubPage(
                "AI models / local-server",
                "Local Model Server · Ollama & LM Studio",
                "Ollama Local Server · Zero-telemetry local inference endpoint.",
                [
                    ["Server", "Ollama Local Server"],
                    ["Endpoint", "http://localhost:11434"],
                    ["Active local model", state.DefaultModel],
                    ["Reasoning effort", "Medium · Local execution"],
                ],
                "back-to-ai-models",
                "Back to AI models");
        }

        return Surface(SurfaceKind.Elevated, "AI models", "Select the default reasoning model for Ino and autonomous background neurons")[
            VStack(Space.Md)[
                SelectBox(
                    "Default reasoning model",
                    state.DefaultModel,
                    Option("qwen3.5:4b", "qwen3.5:4b (Local)")
                        .Key("model-opt-qwen")
                        .Emits(PrefEmit(state, model: "qwen3.5:4b")),
                    Option("claude-sonnet-4-5", "claude-sonnet-4-5")
                        .Key("model-opt-claude")
                        .Emits(PrefEmit(state, model: "claude-sonnet-4-5")),
                    Option("gpt-5", "gpt-5")
                        .Key("model-opt-gpt5")
                        .Emits(PrefEmit(state, model: "gpt-5"))),
                HStack(Space.Sm)[
                    PrimaryButton("Local server (Ollama)", "cpu")
                        .Key("models:local-server")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "models", "local-server")),
                    Button("Configure endpoint sheet", Icons.Sliders)
                        .Key("models:endpoint-sheet")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "models", "", "ollama-config")),
                    Button("API keys & vault", Icons.KeyRound)
                        .Key("models:open-keys")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "keys"))
                ]
            ]
        ];
    }

    private static Node RenderKeysSection(SettingsAppState state)
    {
        (string Provider, string Detail, string SecretName, string Connection, string ButtonId, string ButtonLabel)[] vaults =
        [
            ("Anthropic", "Used for Claude Sonnet & Opus models", "ANTHROPIC_API_KEY", "anthropic", "seal-anthropic-key", "Seal Anthropic key"),
            ("OpenAI", "Used for GPT-5 & reasoning models", "OPENAI_API_KEY", "openai", "seal-openai-key", "Seal OpenAI key"),
            ("Google Gemini", "Used for Gemini 2.5 Pro & Flash", "GOOGLE_API_KEY", "google", "seal-google-key", "Seal Google key"),
        ];

        var cards = vaults.Select(v =>
        {
            var isConfigured = (state.SecretStatuses != null && state.SecretStatuses.TryGetValue(v.SecretName, out var configured) && configured)
                || HasVaultEntry(state, v.SecretName, v.Connection);
            return (Node)VStack(Space.Xs)[
                HStack(Space.Sm)[
                    StatusPill(isConfigured ? StatusKind.Done : StatusKind.Idle, isConfigured ? "Configured" : "Not set"),
                    MutedText(v.SecretName)
                ],
                new SecretCardNode(
                    v.Provider,
                    $"{v.Detail} · {(isConfigured ? "Configured" : "Not set")}",
                    SavedIn: $"Secure Vault · {v.SecretName}",
                    Status: isConfigured ? "saved" : "pending")
            ];
        }).ToArray();

        return Surface(SurfaceKind.Elevated, "API keys", "Keys are sealed in your personal vault and never echoed back after saving")[
            VStack(Space.Md)[
                VStack(Space.Sm, cards),
                HStack(Space.Sm, [
                    .. vaults.Select(v => Button(v.ButtonLabel, Icons.KeyRound)
                        .Key(v.ButtonId)
                        .Small()
                        .Emits(AppSignals.Emit("settings", "set-variable", v.SecretName, $"vault://{v.SecretName}"))
                        .ToNode()),
                    PrimaryButton("Open secure vault window", Icons.KeyRound)
                        .Key("keys:open-vault")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/secure")
                        .ToNode(),
                ])
            ]
        ];
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
            .. (state.Variables ?? []).Select(static variable => (IReadOnlyList<string>)
            [
                variable.Key,
                SettingsDecisions.RedactSecretDisplay(variable.Key, variable.Value),
                "brain",
            ]),
        ];

        return Surface(SurfaceKind.Elevated, "Variables and secrets", "Key-value configuration scoped across local, brain, and system — secret values are always redacted")[
            VStack(Space.Md)[
                StatusPill(StatusKind.Done, $"{rows.Length} scoped entries"),
                Table(
                    "Variables & secrets",
                    ["Key", "Value", "Scope"],
                    rows,
                    sourceNeuronUri: "neuron://local/os.settings/settings")
            ]
        ];
    }

    private static Node RenderBillingSection(SettingsAppState state) =>
        Surface(SurfaceKind.Elevated, "Billing", "Real-time token consumption, credit balance, and Rule-guarded monthly spend caps")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(
                        state.ComputeBudgetExceeded ? StatusKind.Error : StatusKind.Done,
                        state.ComputeBudgetExceeded ? "Monthly cap exceeded" : "Within monthly cap"),
                    MutedText(state.ComputeRowsSummary).Tabular()
                ],
                Grid(3, Space.Md)[
                    StatCard("bill-credits", "Metered spend", state.ComputeSpendUsd, state.ComputeRowsSummary, state.ComputeBudgetExceeded ? BadgeTone.Error : BadgeTone.Done, "wallet"),
                    StatCard("bill-tokens", "Default model", state.DefaultModel, "Active routing", BadgeTone.Accent, "cpu"),
                    StatCard("bill-limit", "Monthly cap", state.ComputeBudgetUsd, state.ComputeBudgetExceeded ? "Cap exceeded" : "Within cap", state.ComputeBudgetExceeded ? BadgeTone.Error : BadgeTone.Neutral, "gauge")
                ],
                HStack(Space.Sm)[
                    (state.MonthlyBudgetMicros == 10_000_000L ? PrimaryButton("Set $10 cap", "gauge") : Button("Set $10 cap", "gauge"))
                        .Key("billing-cap-10")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(10_000_000L))
                        .Emits(AppSignals.Emit("settings", "set-budget", "10000000")),
                    (state.MonthlyBudgetMicros == 50_000_000L ? PrimaryButton("Set $50 cap", "gauge") : Button("Set $50 cap", "gauge"))
                        .Key("billing-cap-50")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(50_000_000L))
                        .Emits(AppSignals.Emit("settings", "set-budget", "50000000")),
                    (state.MonthlyBudgetMicros == 200_000_000L ? PrimaryButton("Set $200 cap", "gauge") : Button("Set $200 cap", "gauge"))
                        .Key("billing-cap-200")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(200_000_000L))
                        .Emits(AppSignals.Emit("settings", "set-budget", "200000000")),
                    DangerButton("Invalid $0 cap", Icons.TriangleAlert)
                        .Key("billing-cap-invalid-zero")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(0L))
                        .OnClick<SettingsAppState>(s => s.ApplyBudgetCap(0L))
                ]
            ]
        ];

    private static Node RenderTeamSection(SettingsAppState state, string? subPage)
    {
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return DetailSubPage(
                $"Team / {subPage}",
                $"PersonPage · {subPage}",
                "Individual circle role and capability grant overrides.",
                [
                    ["Member", subPage],
                    ["Role", "Owner · Full brain administration"],
                    ["Write policy", "Gate 2.0 ApprovalCard required"],
                ],
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

        return Surface(SurfaceKind.Elevated, "Team and permissions", "Manage circle members, roles (Owner, Member, Guest), and capability grants")[
            VStack(Space.Md)[
                StatusPill(StatusKind.Done, $"{memberRows.Length} member(s)"),
                Table(
                    "Brain members",
                    ["Member", "Role", "Status"],
                    memberRows,
                    sourceNeuronUri: "neuron://local/identity/directory"),
                HStack(Space.Sm)[
                    PrimaryButton("Invite teammate", "user-plus")
                        .Key("team:invite")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "team", "", "invite")),
                    Button("Inspect owner grants", "shield-check")
                        .Key("team:owner-grants")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "team", "owner"))
                ]
            ]
        ];
    }

    private static Node RenderNotificationsSection(SettingsAppState state)
    {
        var approvalCards = state.PendingApprovals.Select(static approval => (Node)new ApprovalCardNode(
            $"settings-approval-{approval.PlanId}",
            approval.PlanId,
            approval.Title,
            approval.Detail,
            [new ApprovalRowNode("Subject", "", approval.Subject)],
            SystemName: "Ino",
            Status: "pending",
            Resolve: new Resolve(approval.PlanId))).ToArray();

        return Surface(SurfaceKind.Elevated, "Notifications", "Choose which neural events alert you and review pending approvals")[
            VStack(Space.Md, [
                HStack(Space.Sm)[
                    StatusPill(
                        state.PendingApprovals.Count > 0 ? StatusKind.NeedsYou : StatusKind.Done,
                        state.PendingApprovals.Count > 0 ? $"{state.PendingApprovals.Count} awaiting approval" : "No pending approvals")
                ],
                ToggleSwitch("Needs you · ApprovalCards waiting for confirmation", state.NotifyApprovals)
                    .Key("toggle:notify-approvals")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("approvals")),
                ToggleSwitch("Background jobs & long-running agent completions", state.NotifyBackgroundJobs)
                    .Key("toggle:notify-jobs")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("jobs")),
                ToggleSwitch("Connection expirations & health probe failures", state.NotifyConnectionHealth)
                    .Key("toggle:notify-connections")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("connections")),
                ToggleSwitch("Quiet hours schedule (22:00 – 07:00)", state.QuietHoursEnabled)
                    .Key("toggle:quiet-hours")
                    .OnToggle<SettingsAppState>(s => s.ToggleNotificationPreference("quiet")),
                .. approvalCards,
            ])
        ];
    }

    private static Node RenderKeyboardSection() =>
        Surface(SurfaceKind.Elevated, "Keyboard", "Global hotkeys across Desktop, Conversation, and Editor")[
            Table(
                "Keyboard shortcuts",
                ["Scope", "Action", "Shortcut"],
                [
                    ["Desktop", "Open command palette", "Ctrl+K / ⌘K"],
                    ["Desktop", "Switch recent windows", "Alt+` / ⌥`"],
                    ["Settings", "Dismiss modal sheet or clear search", "Esc"],
                    ["Conversation", "New Ino session", "Ctrl+N / ⌘N"],
                ],
                sourceNeuronUri: "neuron://local/apps/settings#keyboard")
        ];

    private static Node RenderDangerSection() =>
        Surface(SurfaceKind.Elevated, "Danger zone", "Portable brain archive export/restore and irreversible account actions")[
            VStack(Space.Md)[
                StatusPill(StatusKind.NeedsYou, "Two-step confirmation enforced on destructive actions"),
                HStack(Space.Sm)[
                    PrimaryButton("Export brain archive", "download")
                        .Key("danger:export-brain")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "export-brain")),
                    Button("Restore brain archive", "upload")
                        .Key("danger:import-brain")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "import-brain")),
                    DangerButton("Delete account / reset brain", Icons.Trash2)
                        .Key("danger:reset-brain")
                        .Small()
                        .Emits(AppSignals.Emit("settings", "navigate", "danger", "", "confirm-delete"))
                ]
            ]
        ];

    private static Node DetailSubPage(
        string surfaceTitle,
        string heading,
        string subtitle,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string backId,
        string backLabel) =>
        Surface(SurfaceKind.Elevated, surfaceTitle, subtitle)[
            VStack(Space.Md)[
                Heading(heading, 3, subtitle),
                Table(heading, ["Property", "Value"], rows),
                HStack(Space.Sm)[
                    Button(backLabel, "arrow-left")
                        .Key(backId)
                        .Small()
                        .Emits(AppSignals.Emit("settings", "go-back"))
                ]
            ]
        ];

    private static Emit PrefEmit(
        SettingsAppState state,
        string? theme = null,
        string? model = null,
        string? preset = null,
        string? barLayout = null,
        string? themeMode = null,
        string? desktopTone = null)
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

        return AppSignals.Emit("settings", "save-preferences", [.. args]);
    }

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
        string? icon = null) =>
        (selected ? PrimaryButton(label, icon) : Button(label, icon))
            .Key(id)
            .Small()
            .Emits(PrefEmit(state, theme, model, preset, barLayout, themeMode, desktopTone))
            .ToNode();
}
