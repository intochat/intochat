using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Confirmation;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record EngineerInstance(
    int InstanceIndex,
    WorkTreeLease? Lease,
    string[] CompletedTaskIds,
    CoderEdit? Edit = null,
    WorktreeDiagnostic[]? RoslynDiagnostics = null,
    ChangeSet? Commit = null,
    EngineerBuildReport? Build = null,
    EngineerLiveReport? Live = null,
    string Status = "pending",
    string? FailureDetail = null,
    string? Strategy = null,
    EngineerCandidateScore? Score = null,
    EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    EngineerTaskSlice[]? Slices = null,
    string[]? DeletedFiles = null)
{
    [JsonIgnore]
    public string EffectiveStrategy => string.IsNullOrWhiteSpace(Strategy)
        ? EngineerCandidateStrategies.ForCandidate(InstanceIndex).Id
        : Strategy!;

    [JsonIgnore]
    public string StrategyLabel => EngineerCandidateStrategies.LabelFor(EffectiveStrategy, InstanceIndex);

    [JsonIgnore]
    public EngineerCandidateScore EffectiveScore => Score ?? ComputeScore();

    [JsonIgnore]
    public bool IsPublishEligible =>
        Commit is not null
        && Build is { Succeeded: true }
        && Live is { Succeeded: true }
        && EffectiveScore.AllGatesPassed;

    public EngineerCandidateScore ComputeScore()
    {
        var roslynErrors = RoslynDiagnostics?.Count(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var changedLines = Commit is not null ? Commit.Additions + Commit.Deletions : 0;
        return EngineerCandidateScore.Compute(
            Commit?.Commit.Value,
            Commit?.HostRenderedDiff,
            changedLines,
            Build,
            Live,
            roslynErrors);
    }

    public EngineerInstance WithComputedScore() => this with { Score = ComputeScore() };
}

/// <summary>
/// Immutable IntoChat-Engineer pipeline aggregate (<c>flow.engineer</c>) orchestrating 1..N isolated worktree instances
/// across planning, optional human plan ratification, 2-level parallel InoNative slice/worktree execution,
/// build/test, live behavioral verification, deterministic candidate scoring/ranking, and SHA-bound human PR publication.
/// </summary>
internal sealed record EngineerRun
{
    public const int MaxParallelInstances = 8;

    public EngineerRun()
    {
    }

    [JsonConstructor]
    internal EngineerRun(
        string? runId,
        RepositoryId? repository,
        string? goal,
        BranchName? requestedBaseBranch,
        int instanceCount,
        EngineerTaskPlan? plan,
        EngineerInstance[]? instances,
        int selectedInstanceIndex,
        ChangeApproval? approval,
        ChangePublication? publication,
        string? requestedBackend = null,
        int approvalRevision = 0,
        EngineeringHarnessKind harness = EngineeringHarnessKind.InoNative,
        bool requirePlanRatification = false,
        RatifiedPlan? ratifiedPlan = null,
        string[]? requestedCleanupTargets = null,
        EngineerTaskSlice[]? requestedSlices = null,
        string? circleBrainId = null,
        string? matchedProjectBriefKey = null,
        EngineerVerificationPolicy? verification = null)
    {
        RunId = runId ?? string.Empty;
        Repository = repository;
        Goal = goal ?? string.Empty;
        RequestedBaseBranch = requestedBaseBranch;
        InstanceCount = Math.Clamp(instanceCount <= 0 ? 1 : instanceCount, 1, MaxParallelInstances);
        Plan = plan;
        Instances = instances ?? [];
        SelectedInstanceIndex = Math.Max(0, selectedInstanceIndex);
        Approval = approval;
        Publication = publication;
        RequestedBackend = NormalizeBackend(requestedBackend);
        ApprovalRevision = Math.Max(0, approvalRevision);
        Harness = harness;
        RequirePlanRatification = requirePlanRatification;
        RatifiedPlan = ratifiedPlan;
        RequestedCleanupTargets = requestedCleanupTargets;
        RequestedSlices = requestedSlices;
        CircleBrainId = string.IsNullOrWhiteSpace(circleBrainId) ? null : circleBrainId.Trim();
        MatchedProjectBriefKey = string.IsNullOrWhiteSpace(matchedProjectBriefKey) ? null : matchedProjectBriefKey.Trim();
        Verification = verification ?? EngineerVerificationPolicy.Full;
    }

    public string RunId { get; private init; } = string.Empty;

    public RepositoryId? Repository { get; private init; }

    public string Goal { get; private init; } = string.Empty;

    public BranchName? RequestedBaseBranch { get; private init; }

    public int InstanceCount { get; private init; } = 1;

    public string? RequestedBackend { get; private init; }

    public EngineerTaskPlan? Plan { get; private init; }

    public EngineerInstance[] Instances { get; private init; } = [];

    public int SelectedInstanceIndex { get; private init; }

    public ChangeApproval? Approval { get; private init; }

    public ChangePublication? Publication { get; private init; }

    public int ApprovalRevision { get; private init; }

    public EngineeringHarnessKind Harness { get; private init; } = EngineeringHarnessKind.InoNative;

    public bool RequirePlanRatification { get; private init; }

    public RatifiedPlan? RatifiedPlan { get; private init; }

    public string[]? RequestedCleanupTargets { get; private init; }

    public EngineerTaskSlice[]? RequestedSlices { get; private init; }

    public string? CircleBrainId { get; private init; }

    public string? MatchedProjectBriefKey { get; private init; }

    // The gates this run passes. A run stored before the policy existed has none recorded and passes every gate.
    public EngineerVerificationPolicy Verification { get; private init; } = EngineerVerificationPolicy.Full;

    [JsonIgnore]
    public int SelectedCandidateIndex => SelectedInstanceIndex;

    [JsonIgnore]
    public bool IsInitialized => Repository is not null && !string.IsNullOrWhiteSpace(Goal);

    [JsonIgnore]
    public EngineerInstance? SelectedInstance
        => Instances.FirstOrDefault(i => i.InstanceIndex == SelectedInstanceIndex)
            ?? Instances.FirstOrDefault();

    public static EngineerRun Start(
        string? runId,
        RepositoryId repository,
        string goal,
        BranchName? requestedBaseBranch = null,
        int instanceCount = 1,
        string? requestedBackend = null,
        EngineeringHarnessKind harness = EngineeringHarnessKind.InoNative,
        bool requirePlanRatification = false,
        string[]? cleanupTargets = null,
        EngineerTaskSlice[]? slices = null,
        string? circleBrainId = null,
        EngineerVerificationPolicy? verification = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(instanceCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(instanceCount, MaxParallelInstances);

        if (slices is { Length: > 0 })
        {
            var disjoint = new NoOverlappingFileSlices(slices);
            if (DigitalBrain.Rules.Rules.Judge("EngineerRun.Start", disjoint) is { } rejected)
            {
                throw new RuleViolationException(rejected);
            }
        }

        return new EngineerRun
        {
            RunId = runId?.Trim() ?? string.Empty,
            Repository = repository,
            Goal = goal.Trim(),
            RequestedBaseBranch = requestedBaseBranch,
            InstanceCount = instanceCount,
            RequestedBackend = NormalizeBackend(requestedBackend),
            Harness = harness,
            RequirePlanRatification = requirePlanRatification,
            RequestedCleanupTargets = cleanupTargets,
            RequestedSlices = slices,
            CircleBrainId = string.IsNullOrWhiteSpace(circleBrainId) ? null : circleBrainId.Trim(),
            Verification = verification ?? EngineerVerificationPolicy.Full,
        };
    }

    public static EngineerRun FromUntrustedJson(string innerJson, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerJson);
        ArgumentNullException.ThrowIfNull(options);

        var untrusted = JsonSerializer.Deserialize<StartPayload>(innerJson, options)
            ?? throw new ArgumentException("Invalid Engineer start payload.", nameof(innerJson));

        var repository = RepositoryId.Parse(untrusted.Repository ?? string.Empty);
        var requestedBase = string.IsNullOrWhiteSpace(untrusted.RequestedBaseBranch)
            ? null
            : BranchName.FromRequestedBase(untrusted.RequestedBaseBranch);
        var count = untrusted.InstanceCount is null or <= 0 ? 1 : untrusted.InstanceCount.Value;
        var backend = untrusted.Backend ?? untrusted.RequestedBackend;

        return Start(
            untrusted.RunId,
            repository,
            untrusted.Goal ?? string.Empty,
            requestedBase,
            count,
            backend,
            untrusted.Harness ?? EngineeringHarnessKind.InoNative,
            untrusted.RequirePlanRatification ?? false,
            untrusted.CleanupTargets,
            untrusted.Slices,
            untrusted.CircleBrainId,
            // The payload chooses the gates; who switched one off is stamped by the flow from its caller.
            new EngineerVerificationPolicy(
                untrusted.Verification?.BuildAndTest ?? true,
                untrusted.Verification?.LiveVerify ?? true));
    }

    // Records who switched a gate off. A run with every gate on records nobody.
    public EngineerRun WithVerificationSkippedBy(string who)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(who);
        return Verification.SkipsAny ? this with { Verification = Verification with { SkippedBy = who.Trim() } } : this;
    }

    // Records that live-verify could not run because the installation has no live environment. Only the flow calls this, from
    // what the installation has configured; a run whose live-verify a person switched off records nothing here.
    public EngineerRun WithLiveVerifyNotAvailable()
        => Verification.LiveVerify && !Verification.LiveVerifyNotAvailable
            ? this with { Verification = Verification with { LiveVerifyNotAvailable = true } }
            : this;

    // A gate is passed when it ran and succeeded, or when nothing ran because the policy switches it off or, for live-verify,
    // because it was recorded as not available. A result that is there counts: a gate that ran and failed is never forgiven.
    public bool BuildGatePassed(EngineerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Build is { } build ? build.Succeeded : !Verification.BuildAndTest;
    }

    public bool LiveGatePassed(EngineerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Live is { } live ? live.Succeeded : !Verification.LiveVerify || Verification.LiveVerifyNotAvailable;
    }

    public static IReadOnlyList<EngineerInstance> OrderByTournamentRank(IEnumerable<EngineerInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return
        [
            .. instances
                .OrderByDescending(static i => i.IsPublishEligible)
                .ThenByDescending(static i => i.Build is { Succeeded: true } && i.Commit is not null)
                .ThenByDescending(static i => i.Commit is not null)
                .ThenByDescending(static i => i.EffectiveScore.TotalScore)
                .ThenBy(static i => i.Build?.WarningCount ?? int.MaxValue)
                .ThenBy(static i => i.EffectiveScore.ChangedLines > 0 ? i.EffectiveScore.ChangedLines : int.MaxValue)
                .ThenBy(static i => i.InstanceIndex),
        ];
    }

    public static int SelectDefaultWinnerIndex(IReadOnlyList<EngineerInstance> instances)
    {
        if (instances is null || instances.Count == 0)
        {
            return 0;
        }

        return OrderByTournamentRank(instances)[0].InstanceIndex;
    }

    internal static string? NormalizeBackend(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains("volume", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("container", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("cloud", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(WorkTreeBackendKind.ContainerVolume);
        }

        return nameof(WorkTreeBackendKind.LocalGit);
    }

    public EngineerCandidateResult[] RankCandidates()
    {
        if (Instances.Length == 0)
        {
            return [];
        }

        var rankedOrder = OrderByTournamentRank(Instances);
        var rankByInstance = new Dictionary<int, int>(Instances.Length);
        for (var r = 0; r < rankedOrder.Count; r++)
        {
            rankByInstance[rankedOrder[r].InstanceIndex] = r + 1;
        }

        return
        [
            .. Instances
                .OrderBy(static i => i.InstanceIndex)
                .Select(inst =>
                {
                    var lease = inst.Lease;
                    var changed = inst.Commit?.ChangedFiles.Select(static f => f.Value).ToArray()
                        ?? inst.Edit?.TouchedFiles.Select(static f => f.Value).ToArray()
                        ?? [];
                    var score = inst.EffectiveScore;
                    return new EngineerCandidateResult(
                        InstanceIndex: inst.InstanceIndex,
                        Strategy: inst.EffectiveStrategy,
                        StrategyLabel: inst.StrategyLabel,
                        LeaseId: lease?.EffectiveLeaseId ?? string.Create(CultureInfo.InvariantCulture, $"wt-{inst.InstanceIndex}"),
                        Backend: lease?.Backend.ToString() ?? RequestedBackend ?? nameof(WorkTreeBackendKind.LocalGit),
                        BranchName: lease?.BranchName ?? string.Create(CultureInfo.InvariantCulture, $"ino/engineer-w{inst.InstanceIndex + 1}"),
                        Status: inst.Status,
                        CompletedTaskIds: inst.CompletedTaskIds ?? [],
                        ChangedFiles: changed,
                        Rank: rankByInstance.GetValueOrDefault(inst.InstanceIndex, inst.InstanceIndex + 1),
                        IsSelected: inst.InstanceIndex == SelectedInstanceIndex,
                        Score: score,
                        Build: inst.Build,
                        Live: inst.Live,
                        FailureDetail: inst.FailureDetail,
                        Harness: inst.Harness,
                        Slices: inst.Slices,
                        DeletedFiles: inst.DeletedFiles);
                }),
        ];
    }

    public EngineerRun WithPlan(EngineerTaskPlan plan)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Cannot attach a task plan before EngineerRun has a Repository and Goal.");
        }

        ArgumentNullException.ThrowIfNull(plan);
        return this with { Plan = plan };
    }

    public EngineerRun WithDraftPlan(RatifiedPlan draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var disjoint = new NoOverlappingFileSlices(draft.Slices ?? []);
        if (DigitalBrain.Rules.Rules.Judge("EngineerRun.WithDraftPlan", disjoint) is { } rejected)
        {
            throw new RuleViolationException(rejected);
        }

        return this with { RatifiedPlan = draft with { Ratified = false, Harness = Harness } };
    }

    public EngineerRun WithRatifiedPlan(RatifiedPlan ratified)
    {
        ArgumentNullException.ThrowIfNull(ratified);
        var disjoint = new NoOverlappingFileSlices(ratified.Slices ?? []);
        if (DigitalBrain.Rules.Rules.Judge("EngineerRun.WithRatifiedPlan", disjoint) is { } rejected)
        {
            throw new RuleViolationException(rejected);
        }

        var updatedTaskPlan = Plan is null
            ? null
            : Plan with
            {
                CleanupTargets = ratified.CleanupTargets,
                Slices = ratified.Slices,
                CodeGraphDigest = ratified.CodeGraphDigest ?? Plan.CodeGraphDigest,
            };

        return this with
        {
            Plan = updatedTaskPlan,
            RatifiedPlan = ratified with { Ratified = true, Harness = Harness },
        };
    }

    public EngineerRun WithMatchedProjectBrief(string? briefKey)
        => string.IsNullOrWhiteSpace(briefKey) ? this : this with { MatchedProjectBriefKey = briefKey.Trim() };

    public EngineerRun WithLeases(string runId, IReadOnlyList<WorkTreeLease> leases)
    {
        if (Plan is null)
        {
            throw new InvalidOperationException("Cannot attach worktree leases before brainstorm-and-plan completes.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
        {
            throw new ArgumentException("At least one WorkTreeLease is required.", nameof(leases));
        }

        var updated = new EngineerInstance[leases.Count];
        for (var i = 0; i < leases.Count; i++)
        {
            var lease = leases[i];
            var idx = lease.InstanceIndex;
            var strategy = EngineerCandidateStrategies.ForCandidate(idx).Id;
            var prior = Instances.FirstOrDefault(inst => inst.InstanceIndex == idx);
            updated[i] = prior is not null
                ? prior with
                {
                    Lease = lease,
                    Harness = Harness,
                    Strategy = string.IsNullOrWhiteSpace(prior.Strategy) ? strategy : prior.Strategy,
                    Status = prior.Commit is not null ? prior.Status : "prepared",
                }
                : new EngineerInstance(idx, lease, [], Status: "prepared", Strategy: strategy, Harness: Harness);
        }

        return this with
        {
            RunId = runId.Trim(),
            Instances = updated,
        };
    }

    public EngineerRun WithUpdatedInstances(IReadOnlyList<EngineerInstance> instances, int? selectedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var normalized = instances
            .OrderBy(static i => i.InstanceIndex)
            .Select(static i => i.Commit is not null || i.Build is not null || i.Live is not null ? i.WithComputedScore() : i)
            .ToArray();

        return this with
        {
            Instances = normalized,
            SelectedInstanceIndex = selectedIndex ?? SelectDefaultWinnerIndex(normalized),
        };
    }

    public EngineerRun SelectCandidate(int candidateIndex)
    {
        if (!string.IsNullOrWhiteSpace(Publication?.PushedRef))
        {
            throw new InvalidOperationException("Cannot switch winning candidate after branch push has already completed.");
        }

        var candidate = Instances.FirstOrDefault(i => i.InstanceIndex == candidateIndex)
            ?? throw new ArgumentOutOfRangeException(
                nameof(candidateIndex),
                $"Candidate index {candidateIndex} does not exist in this run.");

        var anyImplemented = Instances.Any(static i => i.Commit is not null);
        var anyBuilt = Instances.Any(static i => i.Build is not null);
        var anyVerified = Instances.Any(static i => i.Live is not null);

        if (anyImplemented && candidate.Commit is null)
        {
            throw new InvalidOperationException(
                $"Cannot promote candidate w{candidateIndex + 1}: candidate did not produce a verified commit ({candidate.FailureDetail ?? candidate.Status}).");
        }

        if (anyBuilt && candidate.Build is not { Succeeded: true })
        {
            throw new InvalidOperationException(
                $"Cannot promote candidate w{candidateIndex + 1}: build-and-test verification failed ({candidate.FailureDetail ?? candidate.Build?.Summary ?? "not built"}).");
        }

        if (anyVerified && candidate.Live is not { Succeeded: true })
        {
            throw new InvalidOperationException(
                $"Cannot promote candidate w{candidateIndex + 1}: live behavioral verification failed ({candidate.FailureDetail ?? candidate.Live?.Summary ?? "not verified"}).");
        }

        if (SelectedInstanceIndex == candidateIndex)
        {
            return this;
        }

        return this with
        {
            SelectedInstanceIndex = candidateIndex,
            Approval = null,
            ApprovalRevision = ApprovalRevision + 1,
        };
    }

    public EngineerRun WithApprovalPending(string planId, DateTimeOffset expires)
    {
        var winner = SelectedInstance
            ?? throw new InvalidOperationException("Cannot enter approval-gate without a verified worktree instance.");
        if (winner.Commit is null || !BuildGatePassed(winner) || !LiveGatePassed(winner))
        {
            throw new InvalidOperationException("Cannot enter approval-gate before commit, build-and-test, and live-verify succeed.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return this with { Approval = new ChangeApproval(planId, expires) };
    }

    public Change ToSelectedChange(ChangeSet? overrideCommit = null)
    {
        if (Repository is null)
        {
            return new Change();
        }

        var winner = SelectedInstance;
        var change = Change.Start(RunId, Repository, Goal, RequestedBaseBranch);
        if (winner?.Lease is { } lease)
        {
            change = change.WithWorkTree(string.IsNullOrWhiteSpace(RunId) ? lease.RunId : RunId, lease);
            if (winner.Edit is { } edit)
            {
                change = change.WithCoderEdit(new CoderEditResult(
                    edit.Summary,
                    [.. edit.TouchedFiles.Select(static f => f.Value)],
                    edit.ModelId));
            }

            if ((overrideCommit ?? winner.Commit) is { } commit)
            {
                change = change.WithCommit(commit);
            }
        }

        return change;
    }

    public string EnsureCanPublish(ConfirmationResult? outcome, CommitSha headSha)
    {
        ArgumentNullException.ThrowIfNull(headSha);
        var winner = SelectedInstance
            ?? throw new SecurityException("Refusing to publish: no verified candidate instance exists.");

        if (!BuildGatePassed(winner))
        {
            throw new SecurityException("Refusing to publish: build-and-test verification has not succeeded.");
        }

        if (!LiveGatePassed(winner))
        {
            throw new SecurityException("Refusing to publish: live behavioral verification has not succeeded.");
        }

        if (Approval is { PlanId.Length: > 0 } approval
            && approval.PlanId.Contains(':', StringComparison.Ordinal)
            && !approval.PlanId.EndsWith(":" + headSha.Value, StringComparison.Ordinal))
        {
            throw new SecurityException(
                $"Refusing to publish: approval plan '{approval.PlanId}' is not bound to verified commit SHA '{headSha.Value}'.");
        }

        var change = ToSelectedChange();
        if (Approval is { } currentApproval)
        {
            change = change.WithApprovalPending(currentApproval.PlanId, currentApproval.Expires);
        }

        return change.EnsureCanPublish(outcome, headSha);
    }

    public EngineerRun WithPushed(string approvedBy, CommitSha pushedSha, string pushedRef)
    {
        var winner = SelectedInstance
            ?? throw new InvalidOperationException("Cannot record push without a selected instance.");
        if (winner.Commit is null || Approval is null)
        {
            throw new InvalidOperationException("Cannot record push before commit and approval.");
        }

        if (pushedSha != winner.Commit.Commit)
        {
            throw new SecurityException(
                $"Refusing to record push for '{pushedSha.Value}'; expected verified commit '{winner.Commit.Commit.Value}'.");
        }

        return this with
        {
            Approval = Approval with { ApprovedBy = approvedBy },
            Publication = (Publication ?? new ChangePublication()) with { PushedRef = pushedRef },
        };
    }

    public EngineerRun WithPullRequest(string? pullRequestUrl)
    {
        if (Publication is null || string.IsNullOrWhiteSpace(Publication.PushedRef))
        {
            throw new InvalidOperationException("Cannot record PR checkpoint before push has completed.");
        }

        return this with
        {
            Publication = Publication with
            {
                PullRequestUrl = string.IsNullOrWhiteSpace(pullRequestUrl) ? null : pullRequestUrl.Trim(),
                PullRequestCompleted = true,
            },
        };
    }

    public EngineerRun WithCleaned()
    {
        if (Publication is null || string.IsNullOrWhiteSpace(Publication.PushedRef))
        {
            throw new InvalidOperationException("Cannot record cleanup checkpoint before push has completed.");
        }

        return this with
        {
            Publication = Publication with { Cleaned = true },
        };
    }

    private sealed record StartPayload(
        string? RunId,
        string? Repository,
        string? Goal,
        string? RequestedBaseBranch,
        int? InstanceCount,
        string? Backend = null,
        string? RequestedBackend = null,
        bool? RequirePlanRatification = null,
        EngineeringHarnessKind? Harness = null,
        string[]? CleanupTargets = null,
        EngineerTaskSlice[]? Slices = null,
        string? CircleBrainId = null,
        VerificationPayload? Verification = null);

    private sealed record VerificationPayload(bool? BuildAndTest = null, bool? LiveVerify = null);
}
