using System.Globalization;
using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Engineer;

internal static class EngineerViewRenderer
{
    public const string AppOwner = "app.engineer";

    public static Node RenderRoot(EngineerWorkspaceState state, Rejected? lastRejection)
    {
        ArgumentNullException.ThrowIfNull(state);
        var activeTab = EngineerWorkspaceState.NormalizeTab(state.ActiveTab);

        var stageStatus = state.PrPublished
            ? StatusPill(StatusKind.Done, "PR Published")
            : state.VerificationPassed
                ? StatusPill(StatusKind.NeedsYou, string.Create(CultureInfo.InvariantCulture, $"Awaiting PR Approval (w{state.SelectedInstanceIndex + 1})"))
                : state.PlanApproved
                    ? StatusPill(StatusKind.Working, $"{state.InstanceCount} Worktree(s) Active")
                    : StatusPill(StatusKind.Idle, "Planning");

        var toolbar = VStack(Space.Xs)[
            HeaderBar(
                "IntoChat Engineer",
                $"Autonomous Multi-Worktree Pipeline · Repo: {state.RepositoryId} · Run: {state.ActiveRunId} ({state.BackendMode})",
                stageStatus,
                PrimaryButton("New Run", Icons.Plus)
                    .Key("engineer:new-run")
                    .Small()
                    .OnClickAsync<EngineerApp>(app => app.NewRunCommand()),
                Button("Approve Plan", Icons.Check)
                    .Key("engineer:approve-plan")
                    .Small()
                    .Guard(state.PlanReadyToApprove())
                    .OnClickAsync<EngineerApp>(app => app.ApprovePlanCommand()),
                Button("Run Verify", "play")
                    .Key("engineer:run-verify")
                    .Small()
                    .Guard(state.ReadyForVerification())
                    .OnClickAsync<EngineerApp>(app => app.RunVerifyCommand()),
                PrimaryButton("Approve PR", "git-pull-request")
                    .Key("engineer:approve-pr")
                    .Small()
                    .Guard(state.ReadyForPrApproval())
                    .OnClickAsync<EngineerApp>(app => app.ApprovePrCommand())),
            UiNeuron("ui.command-bar", EngineerApp.CommandBarKey),
            UiNeuron("ui.stepper", EngineerApp.StepperKey)
        ];

        var candidateButtons = state.Candidates
            .Select(c =>
            {
                var label = string.Create(CultureInfo.InvariantCulture, $"Select Candidate w{c.InstanceIndex + 1}");
                var btn = c.InstanceIndex == state.SelectedInstanceIndex
                    ? PrimaryButton(label)
                    : GhostButton(label);
                return (Node)btn
                    .Key(EngineerApp.CandidateSelectKey(c.InstanceIndex))
                    .Small()
                    .Guard(state.ValidCandidateSelection(c.InstanceIndex))
                    .OnClickAsync<EngineerApp>(app => app.SelectCandidateCommand(c.InstanceIndex));
            })
            .ToArray();

        var sidebar = VStack(Space.Md)[
            Surface(SurfaceKind.Subtle, "Parallel Worktrees", $"{state.InstanceCount} isolated lease(s) · {state.BackendMode}")[
                VStack(Space.Sm)[
                    HStack(Space.Xs)[
                        (state.InstanceCount == 1 ? PrimaryButton("1x") : GhostButton("1x"))
                            .Key("engineer:instances:1")
                            .Small()
                            .OnClickAsync<EngineerApp>(app => app.SetInstancesCommand(1, state.BackendMode)),
                        (state.InstanceCount == 2 ? PrimaryButton("2x") : GhostButton("2x"))
                            .Key("engineer:instances:2")
                            .Small()
                            .OnClickAsync<EngineerApp>(app => app.SetInstancesCommand(2, state.BackendMode)),
                        (state.InstanceCount == 3 ? PrimaryButton("3x") : GhostButton("3x"))
                            .Key("engineer:instances:3")
                            .Small()
                            .OnClickAsync<EngineerApp>(app => app.SetInstancesCommand(3, state.BackendMode))
                    ],
                    HStack(Space.Xs)[
                        (state.BackendMode == "LocalGit" ? PrimaryButton("Local Git") : GhostButton("Local Git"))
                            .Key("engineer:backend:local")
                            .Small()
                            .OnClickAsync<EngineerApp>(app => app.SetInstancesCommand(state.InstanceCount, "LocalGit")),
                        (state.BackendMode == "ContainerVolume" ? PrimaryButton("Cloud Volume") : GhostButton("Cloud Volume"))
                            .Key("engineer:backend:volume")
                            .Small()
                            .OnClickAsync<EngineerApp>(app => app.SetInstancesCommand(state.InstanceCount, "ContainerVolume"))
                    ],
                    candidateButtons.Length > 0 ? (Node)VStack(Space.Xs)[candidateButtons] : null,
                    UiNeuron("ui.tree-view", EngineerApp.WorktreeTreeKey)
                ]
            ]
        ];

        var body = VStack(Space.Md)[
            lastRejection is not null
                ? Banner("Domain rule refused command", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            Banner("Pipeline Status", state.StatusBanner, state.PrPublished || state.VerificationPassed ? "success" : "info"),
            UiNeuron("ui.tabs", EngineerApp.WorkspaceTabsKey),
            activeTab switch
            {
                "code" => RenderCodeTab(state),
                "verify" => RenderVerifyTab(state),
                "pr" => RenderPrReviewTab(state),
                _ => RenderPlanTab(state),
            }
        ];

        return BindOwner(
            new AppShellNode(
                Key: "engineer-shell",
                Sidebar: sidebar,
                Body: body,
                Toolbar: toolbar),
            AppOwner);
    }

    private static Node RenderPlanTab(EngineerWorkspaceState state) => VStack(Space.Md)[
        Grid(2, Space.Md)[
            Card("Brainstorm Session (User · Architect · Critic)", $"Goal: {state.Goal}")[
                UiNeuron("ui.chat", EngineerApp.BrainstormChatKey)
            ],
            Card("Rule-Checked Task Graph (EngineerTaskPlan)", $"{state.Tasks.Length} DAG task(s) · Policy max 5 files")[
                VStack(Space.Md)[
                    UiNeuron("ui.kanban", EngineerApp.KanbanKey),
                    UiNeuron("ui.table", EngineerApp.TasksTableKey)
                ]
            ]
        ]
    ];

    private static Node RenderCodeTab(EngineerWorkspaceState state)
    {
        var activeCandidate = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var candidateTag = string.Create(
            CultureInfo.InvariantCulture,
            $"Candidate w{state.SelectedInstanceIndex + 1} (#{state.SelectedInstanceIndex} · {activeCandidate?.StrategyLabel ?? "Surgical Patch"})");

        return VStack(Space.Md)[
            HStack(Space.Sm)[
                StatusPill(StatusKind.Working, candidateTag),
                StatusPill(StatusKind.Idle, $"Branch: {activeCandidate?.BranchName ?? "ino/feature"}"),
                StatusPill(StatusKind.Done, $"File: {state.SelectedFilePath} ({state.SelectedLanguage})")
            ],
            Card($"Live Worktree Source & Diff · {state.SelectedFilePath}", "Roslyn symbol/diagnostic-aware C# & Dart editor")[
                UiNeuron("ui.code-editor", EngineerApp.CodeEditorKey)
            ],
            Card("Task Checkpoints & Diagnostics", "Durable per-task checkpoints across parallel worktrees")[
                UiNeuron("ui.table", EngineerApp.TasksTableKey)
            ]
        ];
    }

    private static Node RenderVerifyTab(EngineerWorkspaceState state) => VStack(Space.Md)[
        Grid(4, Space.Sm)[
            StatCard("kpi-eng-build", "Release Build", state.VerificationPassed ? "Passed" : "Pending", "0 warnings · 0 errors", state.VerificationPassed ? BadgeTone.Done : BadgeTone.Neutral, "hammer"),
            StatCard("kpi-eng-analyze", "Flutter Analyze", state.VerificationPassed ? "Clean" : "Pending", "--no-pub", state.VerificationPassed ? BadgeTone.Done : BadgeTone.Neutral, "check"),
            StatCard("kpi-eng-live", "MCP & Playwright", state.VerificationPassed ? "200 OK" : "Pending", "0 4xx · 0 5xx", state.VerificationPassed ? BadgeTone.Done : BadgeTone.Accent, "globe"),
            StatCard("kpi-eng-traces", "OTel Spans", state.VerificationPassed ? "0 Errors" : "Ready", string.Create(CultureInfo.InvariantCulture, $"{state.DiagnosticsAndTraces.Length} trace entries"), BadgeTone.Accent, "activity")
        ],
        Card("Candidate Tournament Scoreboard, Compiler Diagnostics & Live Traces", "dotnet build -c Release · flutter analyze · dotnet test · ILiveVerifier")[
            UiNeuron("ui.table", EngineerApp.TasksTableKey)
        ]
    ];

    private static Node RenderPrReviewTab(EngineerWorkspaceState state)
    {
        var winner = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var shortSha = winner?.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        var planNote = string.IsNullOrWhiteSpace(state.ConfirmationPlanId) ? string.Empty : $" · Plan: {state.ConfirmationPlanId}";

        return VStack(Space.Md)[
            Surface(
                SurfaceKind.Elevated,
                state.PrPublished
                    ? $"Draft PR Published · {state.PullRequestUrl}"
                    : "SHA-Bound Human Approval Gate (IConfirmationRequests)",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Winning candidate: w{state.SelectedInstanceIndex + 1} (#{state.SelectedInstanceIndex} · {winner?.StrategyLabel ?? "Surgical Patch"} · Score {winner?.TotalScore ?? 0}) · Branch: {winner?.BranchName ?? "ino/feature"} · Commit: {shortSha}{planNote}"))[
                VStack(Space.Sm)[
                    MutedText("Only a human Person can approve pushing the verified commit SHA and opening the GitHub Draft Pull Request. Select any passing candidate w1..wN to switch the promoted diff & SHA-bound confirmation gate."),
                    HStack(Space.Sm)[
                        PrimaryButton("Approve & Open Draft PR", "git-pull-request")
                            .Key("engineer:pr:approve")
                            .Small()
                            .Guard(state.ReadyForPrApproval())
                            .OnClickAsync<EngineerApp>(app => app.ApprovePrCommand()),
                        DangerButton("Clean Worktrees", "trash")
                            .Key("engineer:pr:clean")
                            .Small()
                            .Guard(state.HasActiveWorktrees())
                            .OnClickAsync<EngineerApp>(app => app.CleanWorktreeCommand())
                    ]
                ]
            ],
            Card(
                "Parallel Candidate Tournament Scoreboard",
                string.Create(CultureInfo.InvariantCulture, $"{state.Candidates.Length} candidate worktree(s) · Active winner: w{state.SelectedInstanceIndex + 1} ({shortSha})"))[
                UiNeuron("ui.table", EngineerApp.TasksTableKey)
            ],
            Card(
                string.Create(CultureInfo.InvariantCulture, $"Host-Rendered Unified Diff Preview · Candidate w{state.SelectedInstanceIndex + 1} ({shortSha})"),
                $"Repository: {state.RepositoryId} · Strategy: {winner?.StrategyLabel ?? "Surgical Patch"} · Changed files: {winner?.ChangedFiles.Length ?? 0}")[
                UiNeuron("ui.code-editor", EngineerApp.CodeEditorKey)
            ]
        ];
    }

    private static Node UiNeuron(string kind, string key) => new NeuronRef(kind, key);
}
