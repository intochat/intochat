using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Engineer;

[GenerateSerializer, Alias("os.engineer.candidate-summary")]
public sealed record EngineerCandidateSummary(
    [property: Id(0)] int InstanceIndex,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] string Backend,
    [property: Id(3)] string BranchName,
    [property: Id(4)] string Status,
    [property: Id(5)] string[] ChangedFiles,
    [property: Id(6)] string? CommitSha = null)
{
    public EngineerCandidateSummary() : this(0, "os-run-0", "LocalGit", "ino/feature-run-w1", "prepared", [], null) { }
}

[GenerateSerializer, Alias("os.engineer.workspace")]
public sealed record EngineerWorkspaceState(
    [property: Id(0)] string ActiveRunId,
    [property: Id(1)] string RepositoryId,
    [property: Id(2)] string Goal,
    [property: Id(3)] int InstanceCount,
    [property: Id(4)] string BackendMode,
    [property: Id(5)] string CurrentStage,
    [property: Id(6)] string ActiveTab,
    [property: Id(7)] int SelectedInstanceIndex,
    [property: Id(8)] string SelectedFilePath,
    [property: Id(9)] string SelectedLanguage,
    [property: Id(10)] bool HasPlan,
    [property: Id(11)] bool PlanApproved,
    [property: Id(12)] bool VerificationPassed,
    [property: Id(13)] bool PrPublished,
    [property: Id(14)] bool WorktreesCleaned,
    [property: Id(15)] string StatusBanner,
    [property: Id(16)] EngineerTaskItem[] Tasks,
    [property: Id(17)] EngineerBrainstormTurn[] BrainstormTurns,
    [property: Id(18)] EngineerCandidateSummary[] Candidates,
    [property: Id(19)] string[] DiagnosticsAndTraces,
    [property: Id(20)] string? ActiveFlowId = null,
    [property: Id(21)] string? PullRequestUrl = null)
{
    public static readonly string[] PipelineStageIds =
    [
        "brainstorm-and-plan",
        "prepare-worktree",
        "implement-tasks",
        "build-and-test",
        "live-verify",
        "review-and-pr",
    ];

    public static readonly string[] TabIds = ["plan", "code", "verify", "pr"];

    public EngineerWorkspaceState() : this(
        ActiveRunId: "eng-01",
        RepositoryId: "os",
        Goal: "Add cloud-ready parallel worktree orchestration and Roslyn verification",
        InstanceCount: 2,
        BackendMode: "LocalGit",
        CurrentStage: "brainstorm-and-plan",
        ActiveTab: "plan",
        SelectedInstanceIndex: 0,
        SelectedFilePath: "src/Modules/Catalog/Flows/EngineerFlow.cs",
        SelectedLanguage: "csharp",
        HasPlan: true,
        PlanApproved: false,
        VerificationPassed: false,
        PrPublished: false,
        WorktreesCleaned: false,
        StatusBanner: "Ready · Review User + Architect + Critic plan or start a new multi-worktree run.",
        Tasks: DefaultSeedTasks(),
        BrainstormTurns: DefaultSeedTurns(),
        Candidates: DefaultSeedCandidates(),
        DiagnosticsAndTraces: DefaultSeedTraces())
    {
    }

    [Invariant("ActiveTab is always plan, code, verify, or pr."), JsonIgnore]
    public bool ValidTab => TabIds.Contains(ActiveTab, StringComparer.Ordinal);

    [Invariant("CurrentStage is always one of the 6 canonical pipeline stages."), JsonIgnore]
    public bool ValidStage => PipelineStageIds.Contains(CurrentStage, StringComparer.Ordinal);

    [Invariant("Parallel worktree InstanceCount stays within 1..8."), JsonIgnore]
    public bool ValidInstanceCount => InstanceCount is >= 1 and <= 8;

    public Rule PlanReadyToApprove() => Decision.Rule(
        "plan_not_ready",
        (HasPlan, PlanApproved),
        static x => !x.HasPlan || x.PlanApproved,
        static x => x.PlanApproved ? "Task plan is already approved." : "Run Brainstorm first to draft an EngineerTaskPlan.");

    public Rule ReadyForVerification() => Decision.Rule(
        "plan_not_approved",
        (PlanApproved, PrPublished),
        static x => !x.PlanApproved || x.PrPublished,
        static x => x.PrPublished ? "Run has already published its Draft PR." : "Approve the task plan before running worktree build & live verification.");

    public Rule ReadyForPrApproval() => Decision.Rule(
        "verification_not_passed",
        (VerificationPassed, PrPublished),
        static x => !x.VerificationPassed || x.PrPublished,
        static x => x.PrPublished ? "Draft PR has already been published." : "Complete Build & Test and Live Verify with zero errors before approving PR.");

    public Rule HasActiveWorktrees() => Decision.Rule(
        "worktrees_already_cleaned",
        WorktreesCleaned,
        static cleaned => cleaned,
        "All isolated worktree leases are already cleaned up.");

    public EngineerWorkspaceState SelectTab([Sample("plan", "code", "verify", "pr")] string? tabId)
    {
        var normalized = NormalizeTab(tabId);
        return this with
        {
            ActiveTab = normalized,
            StatusBanner = $"Viewing {TabTitle(normalized)} tab · Stage: {StageTitle(CurrentStage)}",
        };
    }

    public EngineerWorkspaceState SelectStage([Sample("brainstorm-and-plan", "implement-tasks", "live-verify", "review-and-pr")] string? stageId)
    {
        var normalized = NormalizeStage(stageId);
        var mappedTab = normalized switch
        {
            "brainstorm-and-plan" => "plan",
            "prepare-worktree" or "implement-tasks" => "code",
            "build-and-test" or "live-verify" => "verify",
            _ => "pr",
        };
        return this with
        {
            CurrentStage = normalized,
            ActiveTab = mappedTab,
            StatusBanner = $"Stage focused: {StageTitle(normalized)}",
        };
    }

    public EngineerWorkspaceState SetParallelInstances([Sample(1, 2, 3)] int count, [Sample("LocalGit", "ContainerVolume")] string? backend = null)
    {
        var clamped = Math.Clamp(count, 1, 8);
        var mode = string.Equals(backend?.Trim(), "ContainerVolume", StringComparison.OrdinalIgnoreCase)
            ? "ContainerVolume"
            : string.IsNullOrWhiteSpace(backend) ? BackendMode : "LocalGit";

        var candidates = Enumerable.Range(0, clamped)
            .Select(i => new EngineerCandidateSummary(
                InstanceIndex: i,
                LeaseId: string.Create(CultureInfo.InvariantCulture, $"{RepositoryId}-{ActiveRunId}-{i}"),
                Backend: i % 2 == 1 && mode == "ContainerVolume" ? "ContainerVolume" : mode,
                BranchName: clamped == 1
                    ? $"ino/engineer-{ActiveRunId}"
                    : string.Create(CultureInfo.InvariantCulture, $"ino/engineer-{ActiveRunId}-w{i + 1}"),
                Status: PlanApproved ? "prepared" : "pending",
                ChangedFiles:
                [
                    "src/Modules/Catalog/Flows/EngineerFlow.cs",
                    "src/Modules/Roslyn/Editing/WorktreeRoslynAnalyzer.cs",
                    "ui/lib/src/ui/stepper/stepper_widget.dart",
                ]))
            .ToArray();

        return this with
        {
            InstanceCount = clamped,
            BackendMode = mode,
            SelectedInstanceIndex = Math.Min(SelectedInstanceIndex, clamped - 1),
            Candidates = candidates,
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Configured {clamped} parallel {mode} worktree instance(s)."),
        };
    }

    public EngineerWorkspaceState StartNewRun([Sample("Add Roslyn diagnostic check to EngineerFlow")] string? goal, int? instances = null)
    {
        var nextGoal = string.IsNullOrWhiteSpace(goal)
            ? "Implement Roslyn-verified multi-worktree engineering change"
            : goal.Trim();
        var count = Math.Clamp(instances ?? InstanceCount, 1, 8);
        var nextRunId = string.Create(CultureInfo.InvariantCulture, $"eng-{Math.Abs(nextGoal.GetHashCode(StringComparison.Ordinal)) % 900 + 100}");
        var reset = new EngineerWorkspaceState(
            ActiveRunId: nextRunId,
            RepositoryId: RepositoryId,
            Goal: nextGoal,
            InstanceCount: count,
            BackendMode: BackendMode,
            CurrentStage: "brainstorm-and-plan",
            ActiveTab: "plan",
            SelectedInstanceIndex: 0,
            SelectedFilePath: "src/Modules/Catalog/Flows/EngineerFlow.cs",
            SelectedLanguage: "csharp",
            HasPlan: true,
            PlanApproved: false,
            VerificationPassed: false,
            PrPublished: false,
            WorktreesCleaned: false,
            StatusBanner: $"Started new run '{nextRunId}' ({count} worktree instance(s)) · Ready to brainstorm & approve plan.",
            Tasks: DefaultSeedTasks(),
            BrainstormTurns: DefaultSeedTurns(),
            Candidates: DefaultSeedCandidates(),
            DiagnosticsAndTraces: DefaultSeedTraces());
        return reset.SetParallelInstances(count, BackendMode);
    }

    public Decision<EngineerWorkspaceState> ApprovePlan() =>
        Decision.Check("approve-plan", PlanReadyToApprove())
        ?? Decision.Accept(this with
        {
            PlanApproved = true,
            WorktreesCleaned = false,
            CurrentStage = "implement-tasks",
            ActiveTab = "code",
            Candidates =
            [
                .. Candidates.Select((c, idx) => c with
                {
                    Status = "implemented",
                    CommitSha = new string((char)('a' + (idx % 6)), 40),
                }),
            ],
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Plan approved · Prepared {InstanceCount} isolated worktree(s) and checkpointed {Tasks.Length} task(s)."),
        });

    public Decision<EngineerWorkspaceState> CompleteVerification() =>
        Decision.Check("run-verify", ReadyForVerification())
        ?? Decision.Accept(this with
        {
            VerificationPassed = true,
            CurrentStage = "review-and-pr",
            ActiveTab = "verify",
            Candidates =
            [
                .. Candidates.Select(static c => c with { Status = "verified" }),
            ],
            DiagnosticsAndTraces =
            [
                "dotnet build -c Release · 0 warnings, 0 errors (1,420 ms)",
                "flutter analyze --no-pub · No issues found (890 ms)",
                "dotnet test -c Release · Passed: 24, Failed: 0 (1,950 ms)",
                "ILiveVerifier · Host=Healthy, MCP=200 OK, Playwright=UiNeuronPlaywrightExtensions passed, 4xx=0, 5xx=0, ErrorSpans=0",
            ],
            StatusBanner = "Build, Tests & Live Behavioral Verification PASSED (0 warnings, 0 errors, 0 4xx/5xx) · Awaiting human PR approval.",
        });

    public Decision<EngineerWorkspaceState> ApproveAndPublishPr() =>
        Decision.Check("approve-pr", ReadyForPrApproval())
        ?? Decision.Accept(this with
        {
            PrPublished = true,
            WorktreesCleaned = true,
            CurrentStage = "review-and-pr",
            ActiveTab = "pr",
            PullRequestUrl = string.Create(CultureInfo.InvariantCulture, $"https://github.com/intochat/{RepositoryId}/pull/204"),
            Candidates =
            [
                .. Candidates.Select((c, idx) => c with { Status = idx == SelectedInstanceIndex ? "published" : "cleaned" }),
            ],
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Approved by Person · Pushed branch & opened Draft PR https://github.com/intochat/{RepositoryId}/pull/204 · Cleaned {InstanceCount} worktree(s)."),
        });

    public Decision<EngineerWorkspaceState> CleanWorktrees() =>
        Decision.Check("clean-worktree", HasActiveWorktrees())
        ?? Decision.Accept(this with
        {
            WorktreesCleaned = true,
            Candidates = [.. Candidates.Select(static c => c with { Status = "cleaned" })],
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Cleaned all {InstanceCount} isolated worktree lease(s) idempotently."),
        });

    public static string NormalizeTab(string? tab) => (tab ?? "plan").Trim().ToLowerInvariant() switch
    {
        "code" or "editor" or "diff" or "source" => "code",
        "verify" or "verification" or "traces" or "diagnostics" or "test" => "verify",
        "pr" or "review" or "publish" or "approval" => "pr",
        _ => "plan",
    };

    public static string NormalizeStage(string? stage) => (stage ?? "brainstorm-and-plan").Trim().ToLowerInvariant() switch
    {
        "prepare-worktree" or "worktree" => "prepare-worktree",
        "implement-tasks" or "implement" or "edit" => "implement-tasks",
        "build-and-test" or "build" => "build-and-test",
        "live-verify" or "verify" or "live" => "live-verify",
        "review-and-pr" or "approval-gate" or "push" or "pr" or "clean" => "review-and-pr",
        _ => "brainstorm-and-plan",
    };

    public static string TabTitle(string tab) => NormalizeTab(tab) switch
    {
        "code" => "Code & Diff",
        "verify" => "Verification & Traces",
        "pr" => "PR Review",
        _ => "Plan & Brainstorm",
    };

    public static string StageTitle(string stage) => NormalizeStage(stage) switch
    {
        "prepare-worktree" => "Prepare Worktree",
        "implement-tasks" => "Implement Tasks",
        "build-and-test" => "Build & Test",
        "live-verify" => "Live Verify",
        "review-and-pr" => "Review & PR",
        _ => "Brainstorm & Plan",
    };

    private static EngineerTaskItem[] DefaultSeedTasks() =>
    [
        new(
            "task-1-worktree",
            "Provision N isolated worktrees (LocalGit + ContainerVolume)",
            "csharp",
            ["src/Modules/GitHub/WorkTree/WorkTreeService.cs"],
            [],
            "Acquire deterministic WorkTreeLease instances with Triple Git Lock."),
        new(
            "task-2-roslyn",
            "Apply AST-validated C# & Dart changes with task checkpoints",
            "mixed",
            ["src/Modules/Catalog/Flows/EngineerFlow.cs", "ui/lib/src/ui/stepper/stepper_widget.dart"],
            ["task-1-worktree"],
            "Run WorktreeRoslynAnalyzer diagnostics and ICoder surgical edits."),
    ];

    private static EngineerBrainstormTurn[] DefaultSeedTurns() =>
    [
        new("User", "user", "Goal & Scope", "Add cloud-ready parallel worktree orchestration and Roslyn verification."),
        new("Architect", "architect", "DDD & Module Boundaries", "Partition into isolated WorkTreeLease sandboxes (LocalGit & ContainerVolume), checkpoint tasks in EngineerRun, and gate C# edits with WorktreeRoslynAnalyzer."),
        new("Critic", "critic", "Safety & Behavioral Proof", "Require Triple Git Lock SHA-256 verification, Release build + flutter analyze, zero 4xx/5xx live MCP/Playwright probes, and SHA-bound human Person approval before git push."),
    ];

    private static EngineerCandidateSummary[] DefaultSeedCandidates() =>
    [
        new(0, "os-eng-01-0", "LocalGit", "ino/engineer-eng-01-w1", "prepared", ["src/Modules/Catalog/Flows/EngineerFlow.cs", "src/Modules/Roslyn/Editing/WorktreeRoslynAnalyzer.cs"]),
        new(1, "os-eng-01-1", "ContainerVolume", "ino/engineer-eng-01-w2", "prepared", ["src/Modules/Catalog/Flows/EngineerFlow.cs", "ui/lib/src/ui/stepper/stepper_widget.dart"]),
    ];

    private static string[] DefaultSeedTraces() =>
    [
        "roslyn.symbols · Indexed 18 worktree symbols across 2 changed C# files (0 syntax errors)",
        "worktree.lock · Triple Git Lock SHA-256 verified across 2 parallel worktree leases",
    ];
}
