using DigitalBrain.UI;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Bounded section renderer for Apps, Billing, Team &amp; Permissions,
/// Notifications &amp; Approvals, Keyboard shortcuts, and Danger Zone.
/// </summary>
internal static class SettingsWorkspaceAndSystemView
{
    public static Node RenderAppsSection(SettingsAppState state, string? subPage)
    {
        if (string.Equals(subPage, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsViewRenderer.DetailSubPage(
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
            return SettingsViewRenderer.DetailSubPage(
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

        return Surface(SurfaceKind.Elevated, "Apps", "Installed Smart Apps, deployed Synapse units, and capability grants")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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

    public static Node RenderBillingSection(SettingsAppState state) =>
        Surface(SurfaceKind.Elevated, "Billing", "Real-time token consumption, credit balance, and Rule-guarded monthly spend caps")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
                        .Style(OsMixStyles.FilterPill(state.MonthlyBudgetMicros == 10_000_000L))
                        .Guard(SettingsAppState.ValidateBudget(10_000_000L))
                        .Sends(new SettingsSetBudget(10_000_000L)),
                    (state.MonthlyBudgetMicros == 50_000_000L ? PrimaryButton("Set $50 cap", "gauge") : Button("Set $50 cap", "gauge"))
                        .Key("billing-cap-50")
                        .Small()
                        .Style(OsMixStyles.FilterPill(state.MonthlyBudgetMicros == 50_000_000L))
                        .Guard(SettingsAppState.ValidateBudget(50_000_000L))
                        .Sends(new SettingsSetBudget(50_000_000L)),
                    (state.MonthlyBudgetMicros == 200_000_000L ? PrimaryButton("Set $200 cap", "gauge") : Button("Set $200 cap", "gauge"))
                        .Key("billing-cap-200")
                        .Small()
                        .Style(OsMixStyles.FilterPill(state.MonthlyBudgetMicros == 200_000_000L))
                        .Guard(SettingsAppState.ValidateBudget(200_000_000L))
                        .Sends(new SettingsSetBudget(200_000_000L))
                ]
            ]
        ];

    public static Node RenderTeamSection(SettingsAppState state, string? subPage)
    {
        var roster = state.TeamRoster ?? [];
        var grants = state.FriendConnectionGrants ?? [];

        if (!string.IsNullOrWhiteSpace(subPage))
        {
            var isOwner = string.Equals(subPage, "owner", StringComparison.OrdinalIgnoreCase);
            var matchedFriend = roster.FirstOrDefault(f =>
                string.Equals(f.Account.Value, subPage, StringComparison.OrdinalIgnoreCase));
            var displayLabel = isOwner
                ? state.DisplayName
                : matchedFriend is not null && !string.IsNullOrWhiteSpace(matchedFriend.Name)
                    ? $"{matchedFriend.Name} (@{matchedFriend.Account.Value})"
                    : subPage;
            var canReadFiles = grants.Contains($"{subPage}:files.read", StringComparer.OrdinalIgnoreCase);
            var canUseRooms = grants.Contains($"{subPage}:rooms.edit", StringComparer.OrdinalIgnoreCase);

            var detailChildren = new List<Node>
            {
                Heading($"PersonPage · {displayLabel}", 3, "Individual role, connection permissions, and friend access"),
                Table(
                    displayLabel,
                    ["Property", "Value"],
                    [
                        ["Member", displayLabel],
                        ["Role", isOwner ? "Owner · Full brain administration" : "Friend · Cross-brain E2EE peer"],
                        ["Write policy", isOwner ? "Gate 2.0 ApprovalCard required" : "Scoped to offered resources only"],
                    ]),
            };

            if (!isOwner)
            {
                detailChildren.Add(HStack(Space.Sm)[
                    Button(canReadFiles ? "Revoke Files Read" : "Allow Files Read", "folder")
                        .Key($"team:perm-files:{subPage}")
                        .Small()
                        .Sends(new SettingsSetFriendPermission(subPage, "files.read", !canReadFiles))
                        .ToNode(),
                    Button(canUseRooms ? "Revoke Room Apps Edit" : "Allow Room Apps Edit", "check-square")
                        .Key($"team:perm-rooms:{subPage}")
                        .Small()
                        .Sends(new SettingsSetFriendPermission(subPage, "rooms.edit", !canUseRooms))
                        .ToNode(),
                    DangerButton("Unfriend", Icons.Trash2)
                        .Key($"team:unfriend:{subPage}")
                        .Small()
                        .Sends(new SettingsUnfriend(subPage))
                        .ToNode()
                ]);
            }

            detailChildren.Add(HStack(Space.Sm)[
                Button("Back to People & Team", "arrow-left")
                    .Key("back-to-team")
                    .Small()
                    .Sends(new SettingsGoBack())
                    .ToNode()
            ]);

            return Surface(SurfaceKind.Elevated, $"People & Team / {displayLabel}", "Connection permissions and member controls")
                .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
                VStack(Space.Md, [.. detailChildren])
            ];
        }

        IReadOnlyList<string>[] memberRows =
        [
            [state.DisplayName, "Owner", "Active"],
            .. roster.Select(static friend =>
            {
                var label = string.IsNullOrWhiteSpace(friend.Name) ? $"@{friend.Account.Value}" : $"{friend.Name} (@{friend.Account.Value})";
                return (IReadOnlyList<string>)[label, "Friend", "Verified"];
            }),
        ];

        var rosterActions = roster.Select(static friend => Button(
                string.IsNullOrWhiteSpace(friend.Name) ? $"Manage @{friend.Account.Value}" : $"Manage {friend.Name}",
                "user")
            .Key($"team:person:{friend.Account.Value}")
            .Small()
            .Sends(new SettingsNavigate("team", friend.Account.Value))
            .ToNode()).ToArray();

        var sectionNodes = new List<Node>
        {
            StatusPill(StatusKind.Done, $"{memberRows.Length} member(s)"),
        };

        if (!string.IsNullOrWhiteSpace(state.TeamError))
        {
            sectionNodes.Add(Banner("People & Team", state.TeamError, tone: "error"));
        }

        // Friend Codes (24h Mint / Redeem + @handle lookup)
        var codeChildren = new List<Node>
        {
            HStack(Space.Sm)[
                PrimaryButton("Mint 24h friend code", "key-round")
                    .Key("team:mint-code")
                    .Small()
                    .Sends(new SettingsMintFriendCode())
                    .ToNode(),
                Button("Invite teammate", "user-plus")
                    .Key("team:invite")
                    .Small()
                    .Sends(new SettingsNavigate("team", "", "invite"))
                    .ToNode(),
                Button("Inspect owner grants", "shield-check")
                    .Key("team:owner-grants")
                    .Small()
                    .Sends(new SettingsNavigate("team", "owner"))
                    .ToNode()
            ],
        };
        if (!string.IsNullOrWhiteSpace(state.ActiveFriendCode))
        {
            codeChildren.Add(Banner(
                "Active 24h friend code",
                state.ActiveFriendCode,
                tone: "done"));
        }
        if (!string.IsNullOrWhiteSpace(state.HandleLookupResult))
        {
            codeChildren.Add(MutedText($"Lookup: {state.HandleLookupResult}"));
        }

        sectionNodes.Add(Card("Friend codes & @handle lookup", "Mint or redeem a 24h friend code to establish an E2EE cross-brain peer connection")
            .Style(OsMixStyles.Card())[
            VStack(Space.Sm, [.. codeChildren])
        ]);

        sectionNodes.Add(Table(
            "People & Team",
            ["Member", "Role", "Status"],
            memberRows,
            sourceNeuronUri: "neuron://local/identity/directory"));

        if (rosterActions.Length > 0)
        {
            sectionNodes.Add(HStack(Space.Xs, rosterActions));
        }

        sectionNodes.Add(Card(
            "Ino and apps act as the person who asked",
            "Nobody gets more access than their connection allows. Friends only reach explicitly offered rooms, posts, and [Shareable] mini-apps.")
            .Style(OsMixStyles.Card()));

        return Surface(SurfaceKind.Elevated, "People & Team", "Manage friends, 24h friend codes, @handle lookup, roles, and connection permissions")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md, [.. sectionNodes])
        ];
    }

    public static Node RenderNotificationsSection(SettingsAppState state, SettingsChildren children)
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

        return Surface(SurfaceKind.Elevated, "Notifications", "Choose which neural events alert you and review pending approvals")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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

    public static Node RenderKeyboardSection() =>
        Surface(SurfaceKind.Elevated, "Keyboard", "Global hotkeys across Desktop, Conversation, and Editor")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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

    public static Node RenderDangerSection() =>
        Surface(SurfaceKind.Elevated, "Danger zone", "Portable brain archive export/restore and irreversible account actions")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
}
