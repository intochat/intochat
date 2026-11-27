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
        if (!string.IsNullOrWhiteSpace(subPage))
        {
            return SettingsViewRenderer.DetailSubPage(
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

        return Surface(SurfaceKind.Elevated, "Team and permissions", "Manage group members, roles (Owner, Member, Guest), and capability grants")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
