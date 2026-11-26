using System.Globalization;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Visuals;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

internal static class SettingsViewRenderer
{
    public static Node RenderRoot(SettingsAppState state, SettingsChildren children, Rejected? lastRejection = null)
    {
        var section = SettingsAppState.NormalizeSection(state.Section);
        var sectionEntry = SettingsApp.SearchIndex.First(entry => entry.SectionId == section);

        var sidebar = BuildSidebar(state, section, children);
        var contentChildren = new List<Node?>
        {
            lastRejection is not null
                ? Banner("Invalid setting value", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            !string.IsNullOrEmpty(state.Sheet)
                ? RenderModalSheet(state, state.Sheet!)
                : null,
            RenderSectionBody(state, section, state.SubPage, children),
        };

        return AppShell(
            key: "settings-shell",
            sidebar: sidebar,
            body: VStack(Space.Md, contentChildren.ToArray()),
            toolbar: RenderHeader(state, sectionEntry),
            transition: state.ActiveTransition);
    }

    private static Node BuildSidebar(SettingsAppState state, string activeSection, SettingsChildren children)
    {
        var filtered = SettingsAppState.FilterSections(state.SearchQuery);
        var groups = new[] { "Preferences", "Capabilities & Data", "Organization & System" };
        var nodes = new List<Node?>
        {
            SettingsControls.SearchForm(children, state),
            !string.IsNullOrWhiteSpace(state.SearchQuery)
                ? GhostButton("Clear search", "x")
                    .Key("settings:clear-search")
                    .Small()
                    .Sends(new SettingsClearSearch())
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
                    .Sends(new SettingsClearSearch())));
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
                    command: Sends(new SettingsNavigate(entry.SectionId)))).ToArray()));
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
                command: Sends(new SettingsNavigate("danger"))));
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
                    .Sends(new SettingsGoBack())
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
            "ollama-config" => $"Configure local Ollama or LM Studio server endpoint ({state.LocalOllamaEndpoint}) and reasoning effort.",
            "invite" => "Invite a teammate to this brain and assign Owner, Member, or Guest group permissions.",
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
                        .Sends(new SettingsNavigate(state.Section, state.SubPage, ""))
                ]
            ]
        ];
    }

    private static Node RenderSectionBody(SettingsAppState state, string section, string? subPage, SettingsChildren children) => section switch
    {
        "desktop" => RenderDesktopSection(state, children),
        "apps" => RenderAppsSection(state, subPage),
        "connections" => RenderConnectionsSection(state, subPage, children),
        "files" => RenderFilesSection(state),
        "models" => RenderModelsSection(state, subPage, children),
        "keys" => RenderKeysSection(state, children),
        "variables" => RenderVariablesSection(state),
        "billing" => RenderBillingSection(state),
        "team" => RenderTeamSection(state, subPage),
        "notifications" => RenderNotificationsSection(state, children),
        "keyboard" => RenderKeyboardSection(),
        "danger" => RenderDangerSection(),
        _ => RenderGeneralSection(state, children),
    };

    private static Node RenderGeneralSection(SettingsAppState state, SettingsChildren children)
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
                    SettingsControls.ComboBox(children, state, SettingsControls.ThemeMode, "Appearance mode",
                    [
                        new ComboBoxOption("light", "Light"),
                        new ComboBoxOption("dark", "Dark"),
                        new ComboBoxOption("auto", "Auto (follow system)"),
                    ]),
                    HStack(Space.Sm)[
                        PrefButton(state, "pref-fluent-acrylic", "Fluent Acrylic", state.StylePreset == "fluent-acrylic", preset: "fluent-acrylic"),
                        PrefButton(state, "pref-spatial-glass", "Spatial Glass", state.StylePreset == "spatial-glass", preset: "spatial-glass"),
                        PrefButton(state, "pref-cyber-synaptic", "Cyber Synaptic", state.StylePreset == "cyber-synaptic", preset: "cyber-synaptic"),
                        PrefButton(state, "pref-warm-paper", "Warm Paper", state.StylePreset == "warm-paper", preset: "warm-paper"),
                        PrefButton(state, "pref-midnight", "Midnight", state.StylePreset == "midnight", preset: "midnight"),
                        PrefButton(state, "pref-nordic", "Nordic", state.StylePreset == "nordic", preset: "nordic")
                    ],
                    SettingsControls.RegionalForm(children, state),
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

    private static Node RenderDesktopSection(SettingsAppState state, SettingsChildren children) =>
        Surface(SurfaceKind.Elevated, "Desktop & Dock", "Ambient canvas tone, adaptive bar layout, and status indicators")[
            VStack(Space.Md)[
                Heading("Desktop canvas tone", 3, "Choose the warm paper or deep ink surface behind windows."),
                HStack(Space.Sm)[
                    PrefButton(state, "tone-cream", "Cream", state.DesktopTone == "cream", themeMode: state.ThemeMode, desktopTone: "cream"),
                    PrefButton(state, "tone-sand", "Sand", state.DesktopTone == "sand", themeMode: state.ThemeMode, desktopTone: "sand"),
                    PrefButton(state, "tone-peat", "Peat", state.DesktopTone == "peat", themeMode: state.ThemeMode, desktopTone: "peat"),
                    PrefButton(state, "tone-ink", "Ink", state.DesktopTone == "ink", themeMode: state.ThemeMode, desktopTone: "ink")
                ],
                SettingsControls.ComboBox(children, state, SettingsControls.BarLayout, "OS bar layout",
                [
                    new ComboBoxOption("floating-glass", "Floating Glass"),
                    new ComboBoxOption("compact-pill", "Compact Pill"),
                    new ComboBoxOption("classic-bottom", "Classic Bottom"),
                ]),
                Divider("OS bar indicators"),
                VStack(Space.Sm, [.. SettingsControls.Toggles.Where(toggle => toggle.Section == "desktop").Select(toggle => SettingsControls.ToggleSwitch(children, state, toggle))])
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
                .Sends(new SettingsNavigate("apps", alias))
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
                        .Sends(new SettingsNavigate("apps", "ino")),
                    Button("Inspect Creator app", "wand-sparkles")
                        .Key("apps:inspect-creator")
                        .Small()
                        .Sends(new SettingsNavigate("apps", "creator")),
                    Button("Ino MemoryPage", "brain")
                        .Key("apps:inspect-memory")
                        .Small()
                        .Sends(new SettingsNavigate("apps", "memory")),
                    Button("Install from link", "package-plus")
                        .Key("apps:install-link")
                        .Small()
                        .Sends(new SettingsNavigate("apps", "", "install-link"))
                ],
                unitButtons.Length > 0
                    ? HStack(Space.Sm, unitButtons).ToNode()
                    : MutedText("No custom Synapse units deployed yet.").ToNode()
            ]
        ];
    }

    private static Node RenderConnectionsSection(SettingsAppState state, string? subPage, SettingsChildren children)
    {
        var keys = state.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys;
        if (SettingsAppState.IsGitHubSubPage(subPage))
        {
            return RenderGitHubConnectionHub(state, subPage!, children);
        }

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
                            .Sends(new SettingsNavigate("connections", subPage, "add-connection")),
                        Button("Back to Connections", "arrow-left")
                            .Key("conn:back")
                            .Small()
                            .Sends(new SettingsGoBack())
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
                        .Sends(new SettingsNavigate("connections", "", "add-connection")),
                    Button("GitHub connection", "plug")
                        .Key("conn:github")
                        .Small()
                        .Sends(new SettingsNavigate("connections", "github")),
                    Button("Google Drive connection", Icons.Folder)
                        .Key("conn:google-drive")
                        .Small()
                        .Sends(new SettingsNavigate("connections", "google-drive"))
                ]
            ]
        ];
    }

    private static Node RenderGitHubConnectionHub(SettingsAppState state, string subPage, SettingsChildren children)
    {
        var slug = SettingsAppState.ResolveGitHubSlug(subPage, state.GitHubConnectionSlug);
        var connectionKey = SettingsAppState.NormalizeGitHubConnectionKey(slug);
        var isConnected = state.GitHubConnected
            || (state.ConnectionKeys ?? []).Any(k =>
                string.Equals(k, connectionKey, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(slug, "main", StringComparison.OrdinalIgnoreCase) && string.Equals(k, "github", StringComparison.OrdinalIgnoreCase)));

        var repoTarget = string.IsNullOrWhiteSpace(state.GitHubRepository) ? "<owner/repo>" : state.GitHubRepository.Trim();
        const string webhookPath = "/integrations/github/webhook";
        var ghForwardCommand = $"gh webhook forward --repo {repoTarget} --events=issues,issue_comment,pull_request,check_run,check_suite --url=http://localhost:5000/integrations/github/webhook";

        var copyWebhookIntent = AppSignals.Copy(webhookPath);
        var copyGhForwardIntent = AppSignals.Copy(ghForwardCommand);
        var openTasksIntent = AppSignals.OpenApp("tasks", "approvals");
        var openCreatorIntent = AppSignals.OpenApp("creator", state.GitHubInstalledSynapseAlias);

        var policyLabel = state.GitHubTriagePolicyPreset switch
        {
            "auto-10" => "Auto-comment cap: 10 / hr",
            "draft-only" => "Draft-only (0 auto-posts)",
            "reset" => "Policy reset (unrestricted)",
            _ => "Default governance",
        };
        var policyStatusKind = state.GitHubTriagePolicyPreset switch
        {
            "auto-10" or "draft-only" => StatusKind.Done,
            _ => StatusKind.Idle,
        };

        var synapseSummary = !string.IsNullOrWhiteSpace(state.GitHubInstalledSynapseAlias)
            ? $"Installed unit: {state.GitHubInstalledSynapseAlias}"
                + (!string.IsNullOrWhiteSpace(state.GitHubSynapseMode) ? $" ({state.GitHubSynapseMode})" : "")
            : "No issue triage Synapse unit installed yet.";

        return Surface(SurfaceKind.Elevated, $"Connections / {subPage}", "Turnkey GitHub Repository, Webhook & Issue Triage Hub")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    Heading($"GitHub Hub · {slug}", 3, "Fine-Grained PAT vault onboarding, signed webhook forwarding, policy governance, and 1-click Synapse triage."),
                    StatusPill(isConnected ? StatusKind.Done : StatusKind.Idle, isConnected ? "Connected" : "Not configured"),
                    state.GitHubWebhookSecretConfigured
                        ? StatusPill(StatusKind.Done, "Webhook secret sealed")
                        : null
                ],
                Grid(4, Space.Md)[
                    StatCard("gh-stat-prs", "Tracked PRs", $"{state.GitHubPullRequestCount}", isConnected ? connectionKey : "Awaiting sync", BadgeTone.Neutral, "git-pull-request"),
                    StatCard("gh-stat-issues", "Tracked Issues", $"{state.GitHubIssueCount}", "Untrusted-fenced", BadgeTone.Accent, "circle-dot"),
                    StatCard("gh-stat-drafts", "Pending Triage Drafts", $"{state.GitHubPendingDraftCount}", state.GitHubPendingDraftCount > 0 ? "Awaiting approval" : "Clear", state.GitHubPendingDraftCount > 0 ? BadgeTone.Accent : BadgeTone.Done, Icons.MessageSquare),
                    StatCard("gh-stat-webhook", "LastWebhookAt", state.GitHubLastWebhookAt ?? "Never", webhookPath, isConnected ? BadgeTone.Done : BadgeTone.Neutral, "radio")
                ],
                Table(
                    $"Live repository telemetry · {connectionKey}",
                    ["Field", "Value"],
                    [
                        ["Connection key", connectionKey],
                        ["Repository", string.IsNullOrWhiteSpace(state.GitHubRepository) ? "Not configured" : state.GitHubRepository],
                        ["Status", isConnected ? "Connected" : "Not configured"],
                        ["LastWebhookAt", state.GitHubLastWebhookAt ?? "Never"],
                        ["Webhook endpoint", webhookPath],
                        ["CLI forwarder", ghForwardCommand],
                    ]),
                HStack(Space.Sm)[
                    Button("Copy webhook URL", "copy")
                        .Key("settings:github:copy-webhook-url")
                        .Small()
                        .WithIntent(copyWebhookIntent.Kind, copyWebhookIntent.Arg),
                    Button("Copy gh webhook forward", "terminal")
                        .Key("settings:github:copy-gh-forward")
                        .Small()
                        .WithIntent(copyGhForwardIntent.Kind, copyGhForwardIntent.Arg)
                ],
                Card("Fine-Grained PAT & Webhook Onboarding", "Tokens are sealed via SecretRef and ConnectionNeuron; raw credentials never touch SettingsAppState.")[
                    SettingsControls.GitHubPatForm(children, state)
                ],
                Card("Issue Comment Policy Governance (github.issue.comment)", "Set hourly comment limits or draft-only human review before Octokit writes execute.")[
                    VStack(Space.Sm)[
                        HStack(Space.Sm)[
                            StatusPill(policyStatusKind, policyLabel),
                            !string.IsNullOrWhiteSpace(state.GitHubTriagePolicyPlanId)
                                ? StatusPill(StatusKind.NeedsYou, $"Pending plan {state.GitHubTriagePolicyPlanId}")
                                : null
                        ],
                        HStack(Space.Sm)[
                            (string.Equals(state.GitHubTriagePolicyPreset, "auto-10", StringComparison.Ordinal)
                                ? PrimaryButton("Auto-comment (10 / hr)", "shield-check")
                                : Button("Auto-comment (10 / hr)", "shield-check"))
                                .Key("settings:github-policy:auto-10")
                                .Small()
                                .Sends(new SettingsSetGitHubTriagePolicy("auto-10")),
                            (string.Equals(state.GitHubTriagePolicyPreset, "draft-only", StringComparison.Ordinal)
                                ? PrimaryButton("Draft-only (0 auto-posts)", Icons.Hand)
                                : Button("Draft-only (0 auto-posts)", Icons.Hand))
                                .Key("settings:github-policy:draft-only")
                                .Small()
                                .Sends(new SettingsSetGitHubTriagePolicy("draft-only")),
                            Button("Reset policy", Icons.RefreshCw)
                                .Key("settings:github-policy:reset")
                                .Small()
                                .Sends(new SettingsSetGitHubTriagePolicy("reset"))
                        ]
                    ]
                ],
                Card("1-Click Issue Triage Synapse & Cross-Window Studio", "Deploy a deterministic read-only fenced IssueChanged Synapse unit and jump directly to Tasks or Creator.")[
                    VStack(Space.Sm)[
                        HStack(Space.Sm)[
                            StatusPill(
                                !string.IsNullOrWhiteSpace(state.GitHubInstalledSynapseAlias) ? StatusKind.Done : StatusKind.Idle,
                                synapseSummary),
                            !string.IsNullOrWhiteSpace(state.GitHubSynapseInstallStatus)
                                ? MutedText(state.GitHubSynapseInstallStatus).ToNode()
                                : null
                        ],
                        HStack(Space.Sm)[
                            PrimaryButton("Install Triage Synapse (Draft)", Icons.Sparkles)
                                .Key("settings:github-synapse:draft")
                                .Small()
                                .Sends(new SettingsInstallIssueTriageSynapse(connectionKey, "DraftForApproval")),
                            Button("Install Triage Synapse (Auto)", Icons.Play)
                                .Key("settings:github-synapse:auto")
                                .Small()
                                .Sends(new SettingsInstallIssueTriageSynapse(connectionKey, "AutoComment")),
                            Button("Open Tasks Approvals ↗", "list-checks")
                                .Key("settings:github:open-tasks")
                                .Small()
                                .WithIntent(openTasksIntent.Kind, openTasksIntent.Arg),
                            Button("Open in Creator ↗", "wand-sparkles")
                                .Key("settings:github:open-creator")
                                .Small()
                                .WithIntent(openCreatorIntent.Kind, openCreatorIntent.Arg),
                            Button("Back to Connections", "arrow-left")
                                .Key("conn:back")
                                .Small()
                                .Sends(new SettingsGoBack())
                        ]
                    ]
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
                        .Sends(new SettingsNavigate("files", "", "conflict"))
                ]
            ]
        ];
    }

    private static readonly AiProvider[] DisplayedProviders =
    [
        AiProvider.Ollama,
        AiProvider.FoundryLocal,
        AiProvider.OpenAI,
        AiProvider.Anthropic,
        AiProvider.Google,
        AiProvider.XAI,
        AiProvider.OpenRouter,
    ];

    internal static SettingsRoleRow[] DefaultRoles(SettingsAppState state)
    {
        var allRoles = Enum.GetValues<ModelRole>();
        var rows = new SettingsRoleRow[allRoles.Length];
        for (var i = 0; i < allRoles.Length; i++)
        {
            rows[i] = DefaultRoleRow(allRoles[i], state);
        }

        return rows;
    }

    private static SettingsRoleRow DefaultRoleRow(ModelRole role, SettingsAppState state)
    {
        var needed = RoleNeeds.Of(role);
        var choices = KnownModels.All
            .Where(card => (card.Can & needed) == needed)
            .Select(static card => new SettingsModelChoice(card.Id.Value, card.Name))
            .ToArray();
        var selected = role == ModelRole.Chat && !string.IsNullOrWhiteSpace(state.DefaultModel)
            ? state.DefaultModel
            : KnownModels.Defaults.Device.TryGetValue(role, out var deviceId)
                ? deviceId.Value
                : KnownModels.Defaults.Cloud.TryGetValue(role, out var cloudId)
                    ? cloudId.Value
                    : null;
        return new SettingsRoleRow(role, selected, choices);
    }

    private static SettingsProviderRow[] DefaultProviders(SettingsAppState state)
    {
        var deviceView = state.Device;
        var rows = new List<SettingsProviderRow>(DisplayedProviders.Length + (deviceView?.Runtimes.Length ?? 0));
        foreach (var provider in DisplayedProviders)
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
                status = SettingsApp.FormatRuntimeStatus(foundry, installedCount, fallbackUnavailable: "Not available");
            }
            else if (provider == AiProvider.Ollama)
            {
                var ollama = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.Ollama);
                var installedCount = deviceView?.Models.Count(static m =>
                    m.Runtime == RuntimeId.Ollama && m.State is InstallState.Ready or InstallState.Loaded) ?? 0;
                status = ollama is not null
                    ? SettingsApp.FormatRuntimeStatus(ollama, installedCount, fallbackUnavailable: $"Not detected · {ollama.Endpoint?.ToString() ?? state.LocalOllamaEndpoint}")
                    : $"Not detected · {state.LocalOllamaEndpoint}";
            }
            else
            {
                status = provider switch
                {
                    AiProvider.OpenAI when HasVaultEntry(state, "OPENAI_API_KEY", "openai") => "Ready",
                    AiProvider.Anthropic when HasVaultEntry(state, "ANTHROPIC_API_KEY", "anthropic") => "Ready",
                    AiProvider.Google when HasVaultEntry(state, "GOOGLE_API_KEY", "google") => "Ready",
                    _ => "Add a key in API keys",
                };
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
                        SettingsApp.FormatAttachedRuntimeLabel(runtime.Id),
                        compatVisual.IconKey,
                        SettingsApp.FormatRuntimeStatus(runtime, installedCount, fallbackUnavailable: SettingsApp.FormatUnavailableRuntime(runtime))));
                }
            }
        }

        return [.. rows];
    }

    internal static string FormatHardwareLine(Hardware hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        const double gib = 1024d * 1024d * 1024d;
        var parts = new List<string>(3);
        if (hardware.RamBytes > 0)
        {
            var ramGb = Math.Max(1L, (long)Math.Round(hardware.RamBytes / gib, MidpointRounding.AwayFromZero));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ramGb} GB RAM"));
        }
        if (hardware.VramBytes > 0)
        {
            var vramGb = Math.Max(1L, (long)Math.Round(hardware.VramBytes / gib, MidpointRounding.AwayFromZero));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{vramGb} GB VRAM"));
        }

        var accelLabels = hardware.Accelerators
            .Where(static a => a != Accelerator.Cpu)
            .Select(static a => a switch
            {
                Accelerator.Cuda => "CUDA",
                Accelerator.DirectML => "DirectML",
                Accelerator.Npu => "NPU",
                Accelerator.Metal => "Metal",
                _ => a.ToString(),
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (accelLabels.Length > 0)
        {
            parts.Add(string.Join(", ", accelLabels));
        }
        else if (hardware.Accelerators.Contains(Accelerator.Cpu))
        {
            parts.Add("CPU");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatInstallStatus(Install install)
    {
        var pct = (int)Math.Floor(Math.Clamp(install.Percent, 0d, 100d));
        return install.State switch
        {
            InstallState.Queued => "Queued",
            InstallState.Downloading => string.Create(CultureInfo.InvariantCulture, $"Downloading {pct}%"),
            InstallState.Verifying => "Verifying",
            InstallState.Ready => "Ready",
            InstallState.Loaded => "Loaded",
            InstallState.Failed => string.IsNullOrWhiteSpace(install.Error) ? "Failed" : $"Failed · {install.Error}",
            InstallState.Cancelled => "Cancelled",
            _ => install.State.ToString(),
        };
    }

    private static StatusKind InstallStatusKind(InstallState state) => state switch
    {
        InstallState.Ready or InstallState.Loaded => StatusKind.Done,
        InstallState.Queued or InstallState.Downloading or InstallState.Verifying => StatusKind.Working,
        InstallState.Failed => StatusKind.Error,
        _ => StatusKind.Idle,
    };

    private static string RoleDescription(ModelRole role) => role switch
    {
        ModelRole.Chat => "Default conversation",
        ModelRole.Think => "Hard tasks, Engineer, planning",
        ModelRole.Fast => "Titles, routing, System One",
        ModelRole.Hear => "Speech → text",
        ModelRole.Speak => "Text → speech",
        ModelRole.Talk => "Realtime voice-to-voice",
        ModelRole.Embed => "Memory recall",
        ModelRole.Image => "Image generation",
        _ => role.ToString(),
    };

    private static Node RenderModelsSection(SettingsAppState state, string? subPage, SettingsChildren children)
    {
        if (string.Equals(subPage, "web-tools", StringComparison.OrdinalIgnoreCase))
        {
            return RenderWebToolsSubPage(state, children);
        }

        if (string.Equals(subPage, "local-server", StringComparison.OrdinalIgnoreCase))
        {
            return DetailSubPage(
                "AI / local-server",
                "Local Model Server · Ollama & LM Studio",
                "Ollama Local Server · Zero-telemetry local inference endpoint.",
                [
                    ["Server", "Ollama Local Server"],
                    ["Endpoint", state.LocalOllamaEndpoint],
                    ["Active local model", state.DefaultModel],
                    ["Reasoning effort", "Medium · Local execution"],
                ],
                "back-to-ai-models",
                "Back to AI");
        }

        var providers = state.Providers is { Length: > 0 }
            ? state.Providers
            : DefaultProviders(state);
        var hardwareLine = state.Device is { Hardware: { } hw }
            ? FormatHardwareLine(hw)
            : null;
        var providerChildren = new List<Node>(providers.Length + 1);
        if (!string.IsNullOrWhiteSpace(hardwareLine))
        {
            providerChildren.Add(MutedText(hardwareLine).ToNode());
        }
        foreach (var provider in providers)
        {
            providerChildren.Add(HStack(Space.Sm)[
                new TagPill(provider.Label, "neutral", provider.Icon),
                FlexSpacer(),
                StatusPill(
                    provider.Status.StartsWith("Ready", StringComparison.Ordinal) ? StatusKind.Done : StatusKind.Idle,
                    provider.Status)
            ]);
        }

        var installQueue = state.Device?.Queue ?? [];
        var installChildren = new List<Node>(installQueue.Length + 2);
        if (installQueue.Length > 0)
        {
            foreach (var install in installQueue)
            {
                var label = KnownModels.Find(install.Model.Value)?.Name ?? install.Model.Value;
                installChildren.Add(HStack(Space.Sm)[
                    Text(label).ToNode(),
                    MutedText(install.Runtime.Value).Mono().ToNode(),
                    FlexSpacer(),
                    StatusPill(InstallStatusKind(install.State), FormatInstallStatus(install))
                ]);
            }
        }
        else
        {
            installChildren.Add(MutedText("No model installs in progress.").ToNode());
        }
        installChildren.Add(SettingsControls.InstallModelForm(children, state));

        var rolesByKey = state.Roles.ToDictionary(static row => row.Role);
        var roleControls = Enum.GetValues<ModelRole>()
            .Select(role =>
            {
                var row = rolesByKey.TryGetValue(role, out var existing)
                    ? existing
                    : DefaultRoleRow(role, state);
                var options = row.Choices
                    .Select(static opt =>
                    {
                        var label = !opt.Available && !string.IsNullOrWhiteSpace(opt.Reason)
                            ? $"{opt.Label} · {opt.Reason}"
                            : opt.Label;
                        return new ComboBoxOption(opt.Id, label);
                    })
                    .ToArray();
                return SettingsControls.ComboBox(
                    children,
                    state,
                    SettingsControls.RoleKey(role),
                    $"{role} — {RoleDescription(role)}",
                    options);
            })
            .ToArray();

        return Surface(SurfaceKind.Elevated, "AI", "Providers and default models for each AI role")[
            VStack(Space.Md)[
                Card("Connected Client Compute", "Allow this paired device to advertise ICodeSandbox over its P-256 link for sovereign code execution. Defaults ON on Desktop and OFF on Mobile.")[
                    VStack(Space.Sm)[
                        HStack(Space.Sm)[
                            new TagPill(ICodeSandbox.CapabilityId, "neutral", "cpu"),
                            FlexSpacer(),
                            StatusPill(
                                state.AllowClientCodeSandbox ? StatusKind.Done : StatusKind.Idle,
                                state.AllowClientCodeSandbox ? "Enabled · ICodeSandbox advertised" : "Disabled · Local/cloud fallback")
                        ],
                        VStack(Space.Sm, [.. SettingsControls.Toggles.Where(toggle => toggle.Section == "models").Select(toggle => SettingsControls.ToggleSwitch(children, state, toggle))])
                    ]
                ],
                Card("Providers", "Local runtimes and cloud providers available to this brain")[
                    VStack(Space.Sm, [.. providerChildren])
                ],
                Card("Local installs", "Download progress and queue on this device")[
                    VStack(Space.Sm, [.. installChildren])
                ],
                Card("Roles", "Choose the model each capability uses across Ino and background neurons")[
                    VStack(Space.Sm, roleControls)
                ],
                HStack(Space.Sm)[
                    PrimaryButton("Local server (Ollama)", "cpu")
                        .Key("models:local-server")
                        .Small()
                        .Sends(new SettingsNavigate("models", "local-server")),
                    Button("Web & tools", "globe")
                        .Key("models:web-tools")
                        .Small()
                        .Sends(new SettingsNavigate("models", "web-tools")),
                    Button("Configure endpoint sheet", Icons.Sliders)
                        .Key("models:endpoint-sheet")
                        .Small()
                        .Sends(new SettingsNavigate("models", "", "ollama-config")),
                    Button("API keys & vault", Icons.KeyRound)
                        .Key("models:open-keys")
                        .Small()
                        .Sends(new SettingsNavigate("keys"))
                ]
            ]
        ];
    }

    private static Node RenderWebToolsSubPage(SettingsAppState state, SettingsChildren children)
    {
        var selectedProvider = SettingsControls.ResolveWebSearchProvider(state);
        var braveConfigured = IsSecretConfigured(state, "BRAVE_API_KEY", "brave");
        var tavilyConfigured = IsSecretConfigured(state, "TAVILY_API_KEY", "tavily");

        var servers = (state.McpServers ?? [])
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .ToArray();

        var mcpRows = servers
            .Select(static s => (IReadOnlyList<string>)
            [
                string.IsNullOrWhiteSpace(s.Title) ? s.Name : s.Title,
                s.Url,
                s.Connected ? "Connected" : "Disconnected",
                s.ToolCount.ToString(CultureInfo.InvariantCulture),
                s.Error ?? "",
            ])
            .ToArray();

        var mcpActionRows = servers
            .Select(static server => (Node)HStack(Space.Sm)[
                Text(string.IsNullOrWhiteSpace(server.Title) ? server.Name : server.Title),
                MutedText(server.Url),
                FlexSpacer(),
                StatusPill(
                    server.Connected
                        ? StatusKind.Done
                        : !string.IsNullOrWhiteSpace(server.Error)
                            ? StatusKind.Error
                            : StatusKind.Idle,
                    server.Connected
                        ? $"Connected · {server.ToolCount} tool(s)"
                        : !string.IsNullOrWhiteSpace(server.Error)
                            ? server.Error!
                            : "Disconnected"),
                DangerButton("Remove", Icons.Trash2)
                    .Key($"mcp:{server.Name}")
                    .Small()
                    .Sends(new SettingsMcpRemove(server.Name))
            ])
            .ToArray();

        return Surface(SurfaceKind.Elevated, "AI / Web & tools", "Web search provider, search API keys, and connected MCP tool servers")[
            VStack(Space.Md)[
                Card("Web search", "Choose how Ino searches the web. External pages and MCP results are fenced as untrusted content.")[
                    VStack(Space.Sm)[
                        SettingsControls.ComboBox(children, state, SettingsControls.WebSearchProvider, "Web search provider",
                        [
                            new ComboBoxOption(nameof(SearchProvider.Off), "Off"),
                            new ComboBoxOption(nameof(SearchProvider.Searxng), "SearXNG — free"),
                            new ComboBoxOption(nameof(SearchProvider.Brave), "Brave — ≈ $5 / 1k"),
                            new ComboBoxOption(nameof(SearchProvider.Tavily), "Tavily — 1k free / month"),
                            new ComboBoxOption(nameof(SearchProvider.Native), "Chat model's own search — billed by the provider"),
                        ]),
                        string.Equals(selectedProvider, nameof(SearchProvider.Searxng), StringComparison.OrdinalIgnoreCase)
                            ? SettingsControls.SearxngUrlForm(children, state)
                            : null,
                        Divider("Search API keys"),
                        HStack(Space.Sm)[
                            Text("Brave Search"),
                            MutedText("BRAVE_API_KEY · ≈ $5 / 1k"),
                            FlexSpacer(),
                            StatusPill(braveConfigured ? StatusKind.Done : StatusKind.Idle, braveConfigured ? "Configured" : "Not set")
                        ],
                        HStack(Space.Sm)[
                            Text("Tavily"),
                            MutedText("TAVILY_API_KEY · 1k free / month"),
                            FlexSpacer(),
                            StatusPill(tavilyConfigured ? StatusKind.Done : StatusKind.Idle, tavilyConfigured ? "Configured" : "Not set")
                        ],
                        HStack(Space.Sm)[
                            Button("API keys & vault", Icons.KeyRound)
                                .Key("models:open-keys")
                                .Small()
                                .Sends(new SettingsNavigate("keys"))
                        ]
                    ]
                ],
                Card("MCP tool servers", "Streamable HTTP MCP servers whose tools reach Ino")[
                    VStack(Space.Sm)[
                        servers.Length > 0
                            ? Table("MCP servers", ["Title", "URL", "Status", "Tools", "Error"], mcpRows)
                            : MutedText("No MCP servers configured yet.").ToNode(),
                        mcpActionRows.Length > 0
                            ? VStack(Space.Xs, mcpActionRows).ToNode()
                            : null,
                        Divider("Add MCP server"),
                        SettingsControls.McpAddFormControl(children, state)
                    ]
                ],
                HStack(Space.Sm)[
                    Button("Back to AI", "arrow-left")
                        .Key("back-to-ai-web-tools")
                        .Small()
                        .Sends(new SettingsGoBack())
                ]
            ]
        ];
    }

    private static bool IsSecretConfigured(SettingsAppState state, string secretName, string connectionKey)
        => (state.SecretStatuses != null && state.SecretStatuses.TryGetValue(secretName, out var configured) && configured)
           || HasVaultEntry(state, secretName, connectionKey);

    private static Node RenderKeysSection(SettingsAppState state, SettingsChildren children)
    {
        (string Provider, string Detail, string SecretName, string Connection, string ButtonId, string ButtonLabel)[] vaults =
        [
            ("Anthropic", "Used for Claude Sonnet & Opus models", "ANTHROPIC_API_KEY", "anthropic", "seal-anthropic-key", "Seal Anthropic key"),
            ("OpenAI", "Used for GPT-5 & reasoning models", "OPENAI_API_KEY", "openai", "seal-openai-key", "Seal OpenAI key"),
            ("Google Gemini", "Used for Gemini 2.5 Pro & Flash", "GOOGLE_API_KEY", "google", "seal-google-key", "Seal Google key"),
            ("Brave Search", "Web search for agents · ≈ $5 per 1 000 searches", "BRAVE_API_KEY", "brave", "seal-brave-key", "Seal Brave key"),
            ("Tavily", "Web search for agents · 1 000 free per month", "TAVILY_API_KEY", "tavily", "seal-tavily-key", "Seal Tavily key"),
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

        return Surface(SurfaceKind.Elevated, "API keys", "Keys are sealed in your personal vault via SecretRef and never enter neuron state or journals")[
            VStack(Space.Md)[
                VStack(Space.Sm, cards),
                SettingsControls.ApiKeysForm(children, state),
                HStack(Space.Sm)[
                    PrimaryButton("Open secure vault window", Icons.KeyRound)
                        .Key("keys:open-vault")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/secure")
                ]
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
                SettingsAppState.RedactSecretDisplay(variable.Key, variable.Value),
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
                        .Sends(new SettingsSetBudget(10_000_000L)),
                    (state.MonthlyBudgetMicros == 50_000_000L ? PrimaryButton("Set $50 cap", "gauge") : Button("Set $50 cap", "gauge"))
                        .Key("billing-cap-50")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(50_000_000L))
                        .Sends(new SettingsSetBudget(50_000_000L)),
                    (state.MonthlyBudgetMicros == 200_000_000L ? PrimaryButton("Set $200 cap", "gauge") : Button("Set $200 cap", "gauge"))
                        .Key("billing-cap-200")
                        .Small()
                        .Guard(SettingsAppState.ValidateBudget(200_000_000L))
                        .Sends(new SettingsSetBudget(200_000_000L))
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
                "Individual group role and capability grant overrides.",
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
            .. (state.TeamRoster ?? []).Select(static friend =>
            {
                var label = string.IsNullOrWhiteSpace(friend.Name) ? $"@{friend.Account.Value}" : $"{friend.Name} (@{friend.Account.Value})";
                return (IReadOnlyList<string>)[label, "friend", "Verified"];
            }),
        ];

        return Surface(SurfaceKind.Elevated, "Team and permissions", "Manage group members, roles (Owner, Member, Guest), and capability grants")[
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
                        .Sends(new SettingsNavigate("team", "", "invite")),
                    Button("Inspect owner grants", "shield-check")
                        .Key("team:owner-grants")
                        .Small()
                        .Sends(new SettingsNavigate("team", "owner"))
                ]
            ]
        ];
    }

    private static Node RenderNotificationsSection(SettingsAppState state, SettingsChildren children)
    {
        var approvalCards = state.PendingApprovals.Select(static approval =>
        {
            var visual = VisualCatalog.ForApproval(approval.Subject);
            return (Node)new ApprovalCardNode(
                $"settings-approval-{approval.PlanId}",
                approval.PlanId,
                approval.Title,
                approval.Detail,
                [new ApprovalRowNode("Subject", "", approval.Subject)],
                SystemName: "Ino",
                Status: "pending",
                Icon: visual.IconKey,
                CapabilityLabel: visual.Label);
        }).ToArray();

        return Surface(SurfaceKind.Elevated, "Notifications", "Choose which neural events alert you and review pending approvals")[
            VStack(Space.Md, [
                HStack(Space.Sm)[
                    StatusPill(
                        state.PendingApprovals.Count > 0 ? StatusKind.NeedsYou : StatusKind.Done,
                        state.PendingApprovals.Count > 0 ? $"{state.PendingApprovals.Count} awaiting approval" : "No pending approvals")
                ],
                .. SettingsControls.Toggles.Where(toggle => toggle.Section == "notifications").Select(toggle => SettingsControls.ToggleSwitch(children, state, toggle)),
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
                        .Sends(new SettingsExportBrain()),
                    Button("Restore brain archive", "upload")
                        .Key("danger:import-brain")
                        .Small()
                        .Sends(new SettingsImportBrain()),
                    DangerButton("Delete account / reset brain", Icons.Trash2)
                        .Key("danger:reset-brain")
                        .Small()
                        .Sends(new SettingsNavigate("danger", "", "confirm-delete"))
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
                        .Sends(new SettingsGoBack())
                ]
            ]
        ];

    // The save a preference button sends: the current display name, theme and model with the given ones applied; style and bar
    // layout only when a style, bar, mode or tone is given, the mode only when a mode or tone is given.
    private static SettingsSavePreferences PrefSave(
        SettingsAppState state,
        string? theme = null,
        string? model = null,
        string? preset = null,
        string? barLayout = null,
        string? themeMode = null,
        string? desktopTone = null)
    {
        var styled = preset != null || barLayout != null || themeMode != null || desktopTone != null;
        var moded = themeMode != null || desktopTone != null;
        return new SettingsSavePreferences(
            DisplayName: state.DisplayName,
            Theme: theme ?? state.Theme,
            DefaultModel: model ?? state.DefaultModel,
            StylePreset: styled ? preset ?? state.StylePreset : null,
            BarLayout: styled ? barLayout ?? state.BarLayout : null,
            ThemeMode: moded ? themeMode ?? state.ThemeMode : null,
            DesktopTone: desktopTone);
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
            .Sends(PrefSave(state, theme, model, preset, barLayout, themeMode, desktopTone))
            .ToNode();
}
