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
    [property: Id(6)] string? CommitSha = null,
    [property: Id(7)] string Strategy = "surgical-patch",
    [property: Id(8)] string StrategyLabel = "Minimal Surgical Patch",
    [property: Id(9)] int TotalScore = 0,
    [property: Id(10)] int ChangedLines = 0,
    [property: Id(11)] bool BuildPassed = false,
    [property: Id(12)] bool TestsPassed = false,
    [property: Id(13)] bool LiveVerifyPassed = false,
    [property: Id(14)] bool ZeroTelemetryErrors = false,
    [property: Id(15)] string UnifiedDiff = "",
    [property: Id(16)] string Summary = "",
    [property: Id(17)] int Rank = 1,
    [property: Id(18)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(19)] string[]? DeletedFiles = null)
{
    public EngineerCandidateSummary()
        : this(0, "os-run-0", "LocalGit", "ino/feature-run-w1", "prepared", [], null)
    {
    }
}

[GenerateSerializer, Alias("os.engineer.pr-check-item")]
public sealed record EngineerPrCheckItem(
    [property: Id(0)] string Name,
    [property: Id(1)] string Status,
    [property: Id(2)] string? Conclusion = null,
    [property: Id(3)] bool IsRequired = false)
{
    public EngineerPrCheckItem()
        : this("build", "completed", "success", true)
    {
    }
}

[GenerateSerializer, Alias("os.engineer.pr-review-thread")]
public sealed record EngineerPrReviewThread(
    [property: Id(0)] long CommentId,
    [property: Id(1)] string Author,
    [property: Id(2)] string Body,
    [property: Id(3)] string? FilePath = null,
    [property: Id(4)] bool Replied = false,
    [property: Id(5)] string? ReplyBody = null)
{
    public EngineerPrReviewThread()
        : this(0, "reviewer", string.Empty, null, false, null)
    {
    }
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
    [property: Id(21)] string? PullRequestUrl = null,
    [property: Id(22)] string? ConfirmationPlanId = null,
    [property: Id(23)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(24)] string[]? CleanupTargets = null,
    [property: Id(25)] EngineerTaskSlice[]? Slices = null,
    [property: Id(26)] MissionCollaborator[]? Collaborators = null,
    [property: Id(27)] bool AwaitingPlanRatification = false,
    [property: Id(28)] bool LiveViewportPeek = false,
    [property: Id(29)] string? ActiveTraceId = null,
    [property: Id(30)] int? LivePullRequestNumber = null,
    [property: Id(31)] EngineerPrCheckItem[]? LivePrChecks = null,
    [property: Id(32)] EngineerPrReviewThread[]? LivePrReviewThreads = null,
    [property: Id(33)] string? LivePrReviewSummary = null,
    [property: Id(34)] bool LiveRepositoryConnected = false)
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
        StatusBanner: "Ready · Ratify Ino Native parallel slice plan or start a new multi-worktree mission.",
        Tasks: DefaultSeedTasks(),
        BrainstormTurns: DefaultSeedTurns(),
        Candidates: DefaultSeedCandidates(),
        DiagnosticsAndTraces: DefaultSeedTraces(),
        Harness: EngineeringHarnessKind.InoNative,
        CleanupTargets: DefaultSeedCleanupTargets(),
        Slices: DefaultSeedSlices(),
        Collaborators: DefaultSeedCollaborators())
    {
    }

    [JsonIgnore]
    public string[] EffectiveCleanupTargets => CleanupTargets ?? [];

    [JsonIgnore]
    public EngineerTaskSlice[] EffectiveSlices => Slices is { Length: > 0 } s ? s : DefaultSeedSlices();

    [JsonIgnore]
    public MissionCollaborator[] EffectiveCollaborators => Collaborators is { Length: > 0 } c ? c : DefaultSeedCollaborators();

    [JsonIgnore]
    public EngineerPrCheckItem[] EffectiveLivePrChecks => LivePrChecks ?? [];

    [JsonIgnore]
    public EngineerPrReviewThread[] EffectiveLivePrReviewThreads => LivePrReviewThreads ?? [];

    [Invariant("ActiveTab is always plan, code, verify, or pr."), JsonIgnore]
    public bool ValidTab => TabIds.Contains(ActiveTab, StringComparer.Ordinal);

    [Invariant("CurrentStage is always one of the 6 canonical pipeline stages."), JsonIgnore]
    public bool ValidStage => PipelineStageIds.Contains(CurrentStage, StringComparer.Ordinal);

    [Invariant("Parallel worktree InstanceCount stays within 1..8."), JsonIgnore]
    public bool ValidInstanceCount => InstanceCount is >= 1 and <= 8;

    [Invariant("SelectedInstanceIndex always points to a valid candidate slot."), JsonIgnore]
    public bool ValidSelectedCandidate => Candidates.Length == 0
        ? SelectedInstanceIndex == 0
        : SelectedInstanceIndex >= 0 && SelectedInstanceIndex < Candidates.Length;

    public Rule PlanReadyToApprove() => Decision.Rule(
        "plan_not_ready",
        (HasPlan, PlanApproved),
        static x => !x.HasPlan || x.PlanApproved,
        static x => x.PlanApproved ? "Mission plan is already ratified." : "Run Brainstorm first to draft an EngineerTaskPlan.");

    public Rule DisjointSlicesGuard() => new NoOverlappingFileSlices(EffectiveSlices);

    public Rule ReadyForVerification() => Decision.Rule(
        "plan_not_approved",
        (PlanApproved, PrPublished),
        static x => !x.PlanApproved || x.PrPublished,
        static x => x.PrPublished ? "Run has already published its Draft PR." : "Ratify the mission plan before running worktree build & live verification.");

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

    public Rule CanDepart(bool force = false) => Decision.Rule(
        "active_verification_in_flight",
        (CurrentStage, VerificationPassed, PrPublished, WorktreesCleaned, force),
        static x => !x.force
            && !x.VerificationPassed
            && !x.PrPublished
            && !x.WorktreesCleaned
            && x.CurrentStage is "build-and-test" or "live-verify",
        "Verification gates are actively running; wait for completion or pass Force=true to leave Studio.");

    public Rule ValidCandidateSelection(int candidateIndex) => Decision.Rule(
        "invalid_candidate_selection",
        (Candidates, candidateIndex, PrPublished, WorktreesCleaned),
        static x =>
            x.PrPublished
            || x.WorktreesCleaned
            || x.candidateIndex < 0
            || x.candidateIndex >= x.Candidates.Length
            || x.Candidates[x.candidateIndex].Status is "failed" or "refused" or "error",
        static x =>
            x.PrPublished ? "Draft PR has already been published; winning candidate is locked."
            : x.WorktreesCleaned ? "All isolated worktree leases are already cleaned up."
            : x.candidateIndex < 0 || x.candidateIndex >= x.Candidates.Length
                ? string.Create(CultureInfo.InvariantCulture, $"Candidate index {x.candidateIndex} is outside the active tournament range (0..{Math.Max(0, x.Candidates.Length - 1)}).")
                : string.Create(CultureInfo.InvariantCulture, $"Candidate w{x.candidateIndex + 1} failed verification gates and cannot be promoted."));

    public EngineerWorkspaceState SelectTab([Sample("plan", "code", "verify", "pr")] string? tabId)
    {
        var normalized = NormalizeTab(tabId);
        return this with
        {
            ActiveTab = normalized,
            StatusBanner = $"Viewing {TabTitle(normalized)} · Stage: {StageTitle(CurrentStage)}",
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

    public EngineerWorkspaceState ToggleViewportPeek()
        => this with
        {
            LiveViewportPeek = !LiveViewportPeek,
            StatusBanner = !LiveViewportPeek
                ? "Opened Live App Viewport Peek alongside 3D Flow Graph."
                : "Closed Live App Viewport Peek.",
        };

    public EngineerWorkspaceState SetParallelInstances([Sample(1, 2, 3)] int count, [Sample("LocalGit", "ContainerVolume")] string? backend = null)
    {
        var clamped = Math.Clamp(count, 1, 8);
        var mode = string.Equals(backend?.Trim(), "ContainerVolume", StringComparison.OrdinalIgnoreCase)
            ? "ContainerVolume"
            : string.IsNullOrWhiteSpace(backend) ? BackendMode : "LocalGit";

        var candidates = Enumerable.Range(0, clamped)
            .Select(i =>
            {
                var (strategyId, strategyLabel, _) = EngineerCandidateStrategies.ForCandidate(i);
                return new EngineerCandidateSummary(
                    InstanceIndex: i,
                    LeaseId: string.Create(CultureInfo.InvariantCulture, $"{RepositoryId}-{ActiveRunId}-{i}"),
                    Backend: mode,
                    BranchName: clamped == 1
                        ? $"ino/engineer-{ActiveRunId}"
                        : string.Create(CultureInfo.InvariantCulture, $"ino/engineer-{ActiveRunId}-w{i + 1}"),
                    Status: PlanApproved ? "prepared" : "pending",
                    ChangedFiles:
                    [
                        "src/Modules/Catalog/Flows/EngineerFlow.cs",
                        "src/Modules/Roslyn/Editing/WorktreeRoslynAnalyzer.cs",
                        "ui/lib/src/ui/stepper/stepper_widget.dart",
                    ],
                    Strategy: strategyId,
                    StrategyLabel: strategyLabel,
                    Rank: i + 1,
                    Harness: Harness,
                    DeletedFiles: EffectiveCleanupTargets);
            })
            .ToArray();

        return this with
        {
            InstanceCount = clamped,
            BackendMode = mode,
            SelectedInstanceIndex = Math.Clamp(SelectedInstanceIndex, 0, clamped - 1),
            Candidates = candidates,
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Configured {clamped} parallel {mode} worktree instance(s) on {Harness}."),
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
            StatusBanner: $"Started mission '{nextRunId}' ({count} worktree(s), {Harness}) · Ready to ratify plan.",
            Tasks: DefaultSeedTasks(),
            BrainstormTurns: DefaultSeedTurns(),
            Candidates: DefaultSeedCandidates(),
            DiagnosticsAndTraces: DefaultSeedTraces(),
            Harness: Harness,
            CleanupTargets: DefaultSeedCleanupTargets(),
            Slices: DefaultSeedSlices(),
            Collaborators: DefaultSeedCollaborators());
        return reset.SetParallelInstances(count, BackendMode);
    }

    public Decision<EngineerWorkspaceState> PromoteCandidate([Sample(0, 1, 9)] int candidateIndex)
        => PromoteCandidateCore(candidateIndex, filePath: null);

    internal Decision<EngineerWorkspaceState> PromoteCandidateCore(int candidateIndex, string? filePath)
    {
        if (Decision.Check("select-candidate", ValidCandidateSelection(candidateIndex)) is { } rejected)
        {
            return rejected;
        }

        var candidate = Candidates[candidateIndex];
        var nextFile = !string.IsNullOrWhiteSpace(filePath)
            ? filePath.Trim()
            : candidate.ChangedFiles.Contains(SelectedFilePath, StringComparer.Ordinal)
                ? SelectedFilePath
                : candidate.ChangedFiles.FirstOrDefault() ?? SelectedFilePath;
        var nextLang = nextFile.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
        var nextTab = ActiveTab is "code" or "verify" or "pr"
            ? ActiveTab
            : VerificationPassed ? "pr" : "code";
        var nextPlanId = VerificationPassed && !string.IsNullOrWhiteSpace(candidate.CommitSha)
            ? string.Create(CultureInfo.InvariantCulture, $"app:engineer:{ActiveRunId}:{candidate.CommitSha}")
            : ConfirmationPlanId;
        var shortSha = candidate.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";

        return Decision.Accept(this with
        {
            SelectedInstanceIndex = candidateIndex,
            SelectedFilePath = nextFile,
            SelectedLanguage = nextLang,
            ActiveTab = nextTab,
            ConfirmationPlanId = nextPlanId,
            StatusBanner = string.Create(
                CultureInfo.InvariantCulture,
                $"Promoted candidate w{candidateIndex + 1} ({candidate.StrategyLabel}) · Score {candidate.TotalScore} · Commit {shortSha} ({nextFile})"),
        });
    }

    public Decision<EngineerWorkspaceState> ApprovePlan()
    {
        if (Decision.Check("approve-plan", PlanReadyToApprove(), DisjointSlicesGuard()) is { } rejected)
        {
            return rejected;
        }

        var progressedSlices = EffectiveSlices
            .Select(static s => s with { Status = "implementing", Percent = 60 })
            .ToArray();

        return Decision.Accept(this with
        {
            PlanApproved = true,
            AwaitingPlanRatification = false,
            WorktreesCleaned = false,
            CurrentStage = "implement-tasks",
            ActiveTab = "code",
            Slices = progressedSlices,
            Candidates =
            [
                .. Candidates.Select((c, idx) =>
                {
                    var sha = new string((char)('a' + (idx % 6)), 40);
                    var diff = BuildSeedCandidateDiff(idx, c.StrategyLabel, Goal);
                    var lines = EngineerCandidateScore.CountUnifiedDiffLines(diff);
                    return c with
                    {
                        Status = "implemented",
                        CommitSha = sha,
                        UnifiedDiff = diff,
                        ChangedLines = lines,
                    };
                }),
            ],
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Plan ratified · Executing {progressedSlices.Length} parallel Ino slices across {InstanceCount} isolated worktree(s)."),
        });
    }

    public Decision<EngineerWorkspaceState> CompleteVerification()
    {
        if (Decision.Check("run-verify", ReadyForVerification()) is { } rejected)
        {
            return rejected;
        }

        var scoredCandidates = Candidates
            .Select((c, idx) =>
            {
                var sha = string.IsNullOrWhiteSpace(c.CommitSha) ? new string((char)('a' + (idx % 6)), 40) : c.CommitSha!;
                var diff = string.IsNullOrWhiteSpace(c.UnifiedDiff) ? BuildSeedCandidateDiff(idx, c.StrategyLabel, Goal) : c.UnifiedDiff;
                var changedLines = c.ChangedLines > 0 ? c.ChangedLines : EngineerCandidateScore.CountUnifiedDiffLines(diff);
                var build = new EngineerBuildReport(true, true, true, true, 24, 0, 0, 0, [], 1420, "Build=True, Tests=True (0 warnings)");
                var live = new EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], [], "LiveVerify=True (0 errors)");
                var score = EngineerCandidateScore.Compute(sha, diff, changedLines, build, live);
                return c with
                {
                    Status = "verified",
                    CommitSha = sha,
                    UnifiedDiff = diff,
                    ChangedLines = score.ChangedLines,
                    TotalScore = score.TotalScore,
                    BuildPassed = score.BuildPassed,
                    TestsPassed = score.TestsPassed,
                    LiveVerifyPassed = score.LiveVerifyPassed,
                    ZeroTelemetryErrors = score.ZeroTelemetryErrors,
                    Summary = score.Summary,
                    Rank = idx + 1,
                };
            })
            .ToArray();

        var ordered = scoredCandidates
            .OrderByDescending(static c => c.TotalScore)
            .ThenBy(static c => c.ChangedLines)
            .ThenBy(static c => c.InstanceIndex)
            .ToArray();
        var rankMap = ordered
            .Select((c, r) => (c.InstanceIndex, Rank: r + 1))
            .ToDictionary(static p => p.InstanceIndex, static p => p.Rank);
        var rankedCandidates = scoredCandidates
            .Select(c => c with { Rank = rankMap.GetValueOrDefault(c.InstanceIndex, c.InstanceIndex + 1) })
            .ToArray();

        var winningIdx = ordered.FirstOrDefault()?.InstanceIndex ?? 0;
        var winningSha = rankedCandidates.ElementAtOrDefault(winningIdx)?.CommitSha ?? new string('a', 40);
        var completedSlices = EffectiveSlices
            .Select(static s => s with { Status = "verified", Percent = 100 })
            .ToArray();

        return Decision.Accept(this with
        {
            VerificationPassed = true,
            CurrentStage = "review-and-pr",
            ActiveTab = "verify",
            SelectedInstanceIndex = winningIdx,
            Slices = completedSlices,
            Candidates = rankedCandidates,
            ConfirmationPlanId = string.Create(CultureInfo.InvariantCulture, $"app:engineer:{ActiveRunId}:{winningSha}"),
            DiagnosticsAndTraces =
            [
                "dotnet build -c Release · 0 warnings, 0 errors (1,420 ms)",
                "flutter analyze --no-pub · No issues found (890 ms)",
                "dotnet test -c Release · Passed: 24, Failed: 0 (1,950 ms)",
                "ILiveVerifier · Host=Healthy, MCP=200 OK, Playwright=UiNeuronPlaywrightExtensions passed, 4xx=0, 5xx=0, ErrorSpans=0",
            ],
            StatusBanner = string.Create(
                CultureInfo.InvariantCulture,
                $"Build, Tests & Live Behavioral Verification PASSED · Candidate w{winningIdx + 1} ranked #1 (score {rankedCandidates.ElementAtOrDefault(winningIdx)?.TotalScore ?? 0}) · Awaiting human PR approval."),
        });
    }

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
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Approved by Person · Pushed candidate w{SelectedInstanceIndex + 1} & opened Draft PR https://github.com/intochat/{RepositoryId}/pull/204 · Cleaned {InstanceCount} worktree(s)."),
        });

    public Decision<EngineerWorkspaceState> CleanWorktrees() =>
        Decision.Check("clean-worktree", HasActiveWorktrees())
        ?? Decision.Accept(this with
        {
            WorktreesCleaned = true,
            Candidates = [.. Candidates.Select(static c => c with { Status = "cleaned" })],
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Cleaned all {InstanceCount} isolated worktree lease(s) idempotently."),
        });

    [IgnoreExplore]
    public EngineerWorkspaceState ApplyOperationProgress(OperationProgressed progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        var clampedPct = Math.Clamp(progressed.Percent, 0, 100);
        var updatedSlices = EffectiveSlices
            .Select(s => string.Equals(s.SliceId, progressed.SliceId, StringComparison.Ordinal)
                ? s with { Percent = clampedPct, Status = string.IsNullOrWhiteSpace(progressed.Phase) ? s.Status : progressed.Phase }
                : s)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(progressed.SliceId)
            && !updatedSlices.Any(s => string.Equals(s.SliceId, progressed.SliceId, StringComparison.Ordinal)))
        {
            updatedSlices =
            [
                .. updatedSlices,
                new EngineerTaskSlice(
                    SliceId: progressed.SliceId,
                    Title: string.IsNullOrWhiteSpace(progressed.Detail) ? progressed.SliceId : progressed.Detail,
                    OwnedPaths: [],
                    DependsOnSliceIds: [],
                    Status: string.IsNullOrWhiteSpace(progressed.Phase) ? "running" : progressed.Phase,
                    Percent: clampedPct),
            ];
        }

        return this with
        {
            Harness = progressed.Harness,
            Slices = updatedSlices,
            StatusBanner = string.Create(
                CultureInfo.InvariantCulture,
                $"{progressed.Harness} · {progressed.SliceId} ({clampedPct}%) · {progressed.Phase}"),
        };
    }

    [IgnoreExplore]
    public EngineerWorkspaceState SelectChangedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return this;
        }

        var clean = filePath.Trim();
        var lang = clean.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
        return this with
        {
            SelectedFilePath = clean,
            SelectedLanguage = lang,
            ActiveTab = ActiveTab == "plan" ? "code" : ActiveTab,
            StatusBanner = $"Viewing diff · {clean}",
        };
    }

    [IgnoreExplore]
    public EngineerWorkspaceState ApplyArrival(StudioNavArgs? args)
    {
        if (args is null)
        {
            return this;
        }

        var next = this;
        if (!string.IsNullOrWhiteSpace(args.CandidateId)
            && int.TryParse(args.CandidateId.TrimStart('w', 'W'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var candNum))
        {
            var idx = candNum >= 1 && candNum <= next.Candidates.Length ? candNum - 1 : candNum;
            var promoted = next.PromoteCandidateCore(idx, args.FilePath);
            if (promoted.IsAccepted)
            {
                next = promoted.Next;
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.FilePath))
        {
            next = next.SelectChangedFile(args.FilePath!);
        }

        if (!string.IsNullOrWhiteSpace(args.Lens))
        {
            next = next.SelectTab(args.Lens);
        }
        else if (!string.IsNullOrWhiteSpace(args.Mode))
        {
            next = next.SelectTab(args.Mode);
        }

        return next;
    }

    [IgnoreExplore]
    public EngineerWorkspaceState ApplyLivePullRequestReview(
        int? prNumber,
        string? prUrl,
        EngineerPrCheckItem[] checks,
        EngineerPrReviewThread[] threads,
        string? summary)
        => this with
        {
            LiveRepositoryConnected = true,
            LivePullRequestNumber = prNumber ?? LivePullRequestNumber,
            PullRequestUrl = string.IsNullOrWhiteSpace(prUrl) ? PullRequestUrl : prUrl,
            LivePrChecks = checks,
            LivePrReviewThreads = threads,
            LivePrReviewSummary = summary,
        };

    [IgnoreExplore]
    public EngineerWorkspaceState MarkReviewCommentReplied(long commentId, string replyBody)
    {
        var updated = EffectiveLivePrReviewThreads
            .Select(thread => thread.CommentId == commentId
                ? thread with { Replied = true, ReplyBody = replyBody }
                : thread)
            .ToArray();
        return this with
        {
            LivePrReviewThreads = updated,
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Replied to GitHub review comment #{commentId}."),
        };
    }

    public static string NormalizeTab(string? tab) => (tab ?? "plan").Trim().ToLowerInvariant() switch
    {
        "code" or "editor" or "diff" or "changes" or "source" => "code",
        "verify" or "verification" or "traces" or "diagnostics" or "test" => "verify",
        "pr" or "review" or "publish" or "approval" or "ship" => "pr",
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

    public static string NormalizeBackendMode(string? backend, string fallback = "LocalGit")
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return string.IsNullOrWhiteSpace(fallback) ? "LocalGit" : fallback;
        }

        return backend.Contains("volume", StringComparison.OrdinalIgnoreCase)
            || backend.Contains("container", StringComparison.OrdinalIgnoreCase)
            || backend.Contains("cloud", StringComparison.OrdinalIgnoreCase)
            ? "ContainerVolume"
            : "LocalGit";
    }

    public static string TabTitle(string tab) => NormalizeTab(tab) switch
    {
        "code" => "Changes & Diff",
        "verify" => "Verification",
        "pr" => "PR Review",
        _ => "3D Flow & Plan",
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

    private static string BuildSeedCandidateDiff(int index, string strategyLabel, string goal)
        => index == 0
            ? $$"""
                diff --git a/src/Modules/Catalog/Flows/EngineerFlow.cs b/src/Modules/Catalog/Flows/EngineerFlow.cs
                --- a/src/Modules/Catalog/Flows/EngineerFlow.cs
                +++ b/src/Modules/Catalog/Flows/EngineerFlow.cs
                @@ -19,3 +19,7 @@ namespace IntoChat.OS.Modules.Apps.Flows;
                 internal sealed class EngineerFlow : Flow<EngineerRun>, IFlowStarter
                 {
                +    // Candidate w1 ({{strategyLabel}}): {{goal}}
                +    public const string DefaultStrategy = "surgical-patch";
                +    public bool SupportsParallelTournament => true;
                 }
                """
            : $$"""
                diff --git a/src/Modules/Catalog/Flows/EngineerFlow.cs b/src/Modules/Catalog/Flows/EngineerFlow.cs
                --- a/src/Modules/Catalog/Flows/EngineerFlow.cs
                +++ b/src/Modules/Catalog/Flows/EngineerFlow.cs
                @@ -19,3 +19,12 @@ namespace IntoChat.OS.Modules.Apps.Flows;
                 internal sealed class EngineerFlow : Flow<EngineerRun>, IFlowStarter
                 {
                +    // Candidate w{{index + 1}} ({{strategyLabel}}): {{goal}}
                +    public static EngineerCandidateScore RankVariant(EngineerBuildReport build, EngineerLiveReport live, int changedLines)
                +        => EngineerCandidateScore.Compute(
                +            commitSha: null,
                +            unifiedDiff: string.Empty,
                +            changedLines: changedLines,
                +            build: build,
                +            live: live);
                 }
                """;

    private static string[] DefaultSeedCleanupTargets() =>
    [
        "src/IntoChat.OS.Apps/Creator/LegacyPipelineCards.cs",
        "src/IntoChat.OS.Apps/Engineer/StackedKpiHeader.cs",
    ];

    private static EngineerTaskSlice[] DefaultSeedSlices() =>
    [
        new(
            SliceId: "slice-1",
            Title: "Worktree Contracts & Roslyn Guard",
            OwnedPaths: ["src/Modules/GitHub/WorkTree/WorkTreeService.cs", "src/Modules/Roslyn/Editing/WorktreeRoslynAnalyzer.cs"],
            DependsOnSliceIds: [],
            Status: "planned",
            Percent: 25,
            AssignedAgentKey: "ino.slice-1",
            Language: "csharp"),
        new(
            SliceId: "slice-2",
            Title: "Flow Orchestrator & Dart Mirrors",
            OwnedPaths: ["src/Modules/Catalog/Flows/EngineerFlow.cs", "ui/lib/src/ui/stepper/stepper_widget.dart"],
            DependsOnSliceIds: [],
            Status: "planned",
            Percent: 25,
            AssignedAgentKey: "ino.slice-2",
            Language: "mixed"),
    ];

    private static MissionCollaborator[] DefaultSeedCollaborators() =>
    [
        new("person:owner", "Owner", "person", "ratify-plan", Active: true),
        new("ino.native", "Ino Native Swarm", "ino-agent", "slice-1", Active: true),
    ];

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
        new(
            InstanceIndex: 0,
            LeaseId: "os-eng-01-0",
            Backend: "LocalGit",
            BranchName: "ino/engineer-eng-01-w1",
            Status: "prepared",
            ChangedFiles: ["src/Modules/Catalog/Flows/EngineerFlow.cs", "src/Modules/Roslyn/Editing/WorktreeRoslynAnalyzer.cs"],
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            Rank: 1),
        new(
            InstanceIndex: 1,
            LeaseId: "os-eng-01-1",
            Backend: "ContainerVolume",
            BranchName: "ino/engineer-eng-01-w2",
            Status: "prepared",
            ChangedFiles: ["src/Modules/Catalog/Flows/EngineerFlow.cs", "ui/lib/src/ui/stepper/stepper_widget.dart"],
            Strategy: "clean-refactor",
            StrategyLabel: "Alternative Strategy & Refactor",
            Rank: 2),
    ];

    private static string[] DefaultSeedTraces() =>
    [
        "roslyn.symbols · Indexed 18 worktree symbols across 2 changed C# files (0 syntax errors)",
        "worktree.lock · Triple Git Lock SHA-256 verified across 2 parallel worktree leases",
    ];
}
