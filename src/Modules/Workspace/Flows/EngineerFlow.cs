using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Flows.Rules;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Autonomous multi-stage engineering durable flow (<c>flow.engineer</c>) orchestrating 1..N isolated worktrees
/// across brainstorm/plan, optional human plan ratification, worktree preparation, 2-level parallel InoNative
/// (or optional CLI) slice execution, build/test, live verification, deterministic candidate scoring/ranking,
/// human candidate promotion, SessionJournal writeback, and SHA-bound human approval before branch push, draft PR, and cleanup.
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed class EngineerFlow(
    IWorkTree? workTree = null,
    ICoder? coder = null,
    IEngineerPlanner? planner = null,
    IEngineerBuildVerifier? buildVerifier = null,
    ILiveVerifier? liveVerifier = null,
    IWorktreeRoslyn? roslyn = null,
    IConfirmationRequests? confirmationRequests = null,
    IEngineeringHarness? engineeringHarness = null,
    IServiceProvider? services = null,
    IRepository? repository = null) : Flow<EngineerRun>, IFlowStarter, IEngineerFlow
{
    public const string Alias = IEngineerRuns.EngineerFlow;
    public const string ConfirmationKind = "engineer.publish";
    public const int MaxBuildRepairRounds = 3;
    private const string PlanPrefix = "app:engineer";

    public static readonly TimeSpan ApprovalWindow = EngineerPipelineStages.DefaultApprovalWindow;

    // How long a run waits at live-verify before it looks again for a live environment.
    public static readonly TimeSpan LiveEnvironmentRecheck = TimeSpan.FromMinutes(30);

    public const string LiveVerifyNotAvailableReason =
        "live-verify not available: no live environment is configured on this installation (no candidate verifier and no live endpoint), so the change was NOT live-verified; build-and-test still gated it and a person still approves it.";

    public static readonly string[] Names =
    [
        "brainstorm-and-plan",
        "prepare-worktree",
        "implement-tasks",
        "build-and-test",
        "live-verify",
        "approval-gate",
        "push",
        "pr",
        "clean",
    ];

    internal static readonly JsonSerializerOptions Json = EngineerPipelineStages.Json;

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<EngineerFlow>();

    protected override IReadOnlyList<string> Steps => Names;

    private IWorkTree WorkTree => workTree
        ?? services?.GetService(typeof(IWorkTree)) as IWorkTree
        ?? throw new InvalidOperationException("IWorkTree service is not registered.");

    private ICoder? OptionalCoder => coder
        ?? services?.GetService(typeof(ICoder)) as ICoder;

    private IEngineerPlanner Planner => planner
        ?? services?.GetService(typeof(IEngineerPlanner)) as IEngineerPlanner
        ?? throw new InvalidOperationException("IEngineerPlanner service is not registered.");

    private IEngineerBuildVerifier BuildVerifier => buildVerifier
        ?? services?.GetService(typeof(IEngineerBuildVerifier)) as IEngineerBuildVerifier
        ?? throw new InvalidOperationException("IEngineerBuildVerifier service is not registered.");

    private ILiveVerifier LiveVerifier => liveVerifier
        ?? services?.GetService(typeof(ILiveVerifier)) as ILiveVerifier
        ?? throw new InvalidOperationException("ILiveVerifier service is not registered.");

    private IWorktreeRoslyn Roslyn => roslyn
        ?? services?.GetService(typeof(IWorktreeRoslyn)) as IWorktreeRoslyn
        ?? UnavailableWorktreeRoslyn.Instance;

    private IConfirmationRequests ConfirmationRequests =>
        confirmationRequests
        ?? services?.GetService(typeof(IConfirmationRequests)) as IConfirmationRequests
        ?? Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    // Every gate is on unless the payload switches one off, and only a person or the kernel may do that: the run then records who.
    public async Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        var run = EngineerRun.FromUntrustedJson(innerJson, Json);
        var caller = Context.Caller;
        await Check("Start", new PersonSwitchesVerificationOff(run.Verification, caller)).ConfigureAwait(true);
        if (run.Verification.SkipsAny)
        {
            run = run.WithVerificationSkippedBy(DigitalBrain.Access.CallerRules.ActsFor(caller) ?? "kernel");
        }

        await Start(title, owner, parentFlowId, run).ConfigureAwait(true);
    }

    public Task<FlowHead> Head() => Task.FromResult(EngineerPipelineStages.BuildHead(
        Names,
        State.Owner,
        State.TraceId,
        State.ParentFlowId,
        State.Started,
        State.Inner,
        State.Title,
        State.Status,
        State.Done,
        State.Ended));

    public async Task RatifyPlan(
        string[]? approvedCleanupTargets = null,
        EngineerTaskSlice[]? slices = null,
        string? ratifiedBy = null)
    {
        if (State.IsOver)
        {
            throw new InvalidOperationException($"Cannot ratify plan on finished flow '{Id}' ({State.Status}).");
        }

        var currentInner = State.Inner;
        var basePlan = currentInner.Plan
            ?? throw new InvalidOperationException($"Flow '{Id}' does not have a drafted EngineerTaskPlan to ratify yet.");

        var effectiveCleanup = approvedCleanupTargets
            ?? currentInner.RatifiedPlan?.CleanupTargets
            ?? basePlan.CleanupTargets
            ?? currentInner.RequestedCleanupTargets
            ?? [];
        var effectiveSlices = slices is { Length: > 0 }
            ? slices
            : currentInner.RatifiedPlan?.Slices is { Length: > 0 } existingSlices
                ? existingSlices
                : EngineerTaskSlices.FromTaskPlan(basePlan);

        await Check("RatifyPlan", new NoOverlappingFileSlices(effectiveSlices)).ConfigureAwait(true);

        var candidatePlan = basePlan with
        {
            CleanupTargets = effectiveCleanup,
            Slices = effectiveSlices,
        };
        var maxFiles = Math.Max(
            ResolvePolicy(currentInner.Repository?.Value).MaxChangedFiles,
            effectiveCleanup.Length + effectiveSlices.SelectMany(static s => s.OwnedPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (candidatePlan.Validate(maxFiles) is { } validationError)
        {
            throw new ArgumentException(validationError, nameof(slices));
        }

        var ratified = new RatifiedPlan(
            Goal: currentInner.Goal,
            ArchitectureSummary: candidatePlan.ArchitectureSummary,
            CleanupTargets: effectiveCleanup,
            Slices: effectiveSlices,
            Ratified: true,
            RatifiedBy: string.IsNullOrWhiteSpace(ratifiedBy) ? "owner" : ratifiedBy.Trim(),
            RatifiedAt: Context.Time.GetUtcNow(),
            Harness: currentInner.Harness,
            CodeGraphDigest: candidatePlan.CodeGraphDigest ?? currentInner.RatifiedPlan?.CodeGraphDigest);

        var nextInner = currentInner.WithPlan(candidatePlan).WithRatifiedPlan(ratified);
        await Save(
            State with { Inner = nextInner },
            new FlowStep(
                Id.ToString(),
                "brainstorm-and-plan",
                StepOutcome.Started,
                string.Create(CultureInfo.InvariantCulture, $"Ratified plan ({effectiveSlices.Length} slice(s), {effectiveCleanup.Length} cleanup target(s))."))).ConfigureAwait(true);

        if (State is { Status: FlowStatus.Waiting, Waiting: { Step: "brainstorm-and-plan", Reason: WaitReason.External } })
        {
            await Resume("brainstorm-and-plan", WaitOutcome.Approved, ratified.RatifiedBy).ConfigureAwait(true);
        }
    }

    public async Task SelectCandidate(int candidateIndex)
    {
        if (State.IsOver)
        {
            throw new InvalidOperationException($"Cannot select a candidate on finished flow '{Id}' ({State.Status}).");
        }

        var currentInner = State.Inner;
        var nextInner = currentInner.SelectCandidate(candidateIndex);
        var winner = nextInner.SelectedInstance
            ?? throw new InvalidOperationException($"Candidate index {candidateIndex} is unavailable.");

        if (State is { Status: FlowStatus.Waiting, Waiting: { Step: "approval-gate", Reason: WaitReason.Approval } waiting })
        {
            var verifiedCommit = winner.Commit
                ?? throw new InvalidOperationException($"Candidate w{candidateIndex + 1} has no verified CommitSha.");

            var previousPlanId = waiting.ConfirmationPlanId ?? currentInner.Approval?.PlanId;
            if (currentInner.SelectedInstanceIndex == candidateIndex
                && !string.IsNullOrWhiteSpace(previousPlanId)
                && previousPlanId.EndsWith(":" + verifiedCommit.Commit.Value, StringComparison.Ordinal))
            {
                return;
            }

            EngineerPipelineStages.EnsureCardAcceptsDiff(
                nextInner.ToSelectedChange,
                verifiedCommit,
                "approval-gate");

            var now = Context.Time.GetUtcNow();
            var expires = waiting.Expires ?? now.Add(ApprovalWindow);
            var runId = EffectiveRunId(nextInner);
            var basePlanId = BuildPlanId(runId, verifiedCommit.Commit.Value);
            var requests = ConfirmationRequests;
            var existingOutcome = await requests.Outcome(basePlanId).ConfigureAwait(true);

            var newPlanId = existingOutcome is null && !string.Equals(basePlanId, previousPlanId, StringComparison.Ordinal)
                ? basePlanId
                : EngineerPipelineStages.BuildPlanId(
                    string.Create(CultureInfo.InvariantCulture, $"{PlanPrefix}:r{Math.Max(1, nextInner.ApprovalRevision)}"),
                    runId,
                    verifiedCommit.Commit.Value);

            nextInner = nextInner.WithApprovalPending(newPlanId, expires);
            var nextWaiting = waiting with { ConfirmationPlanId = newPlanId, Expires = expires };
            var signals = new List<Signal>
            {
                new FlowWaiting(Id.ToString(), "approval-gate", WaitReason.Approval, newPlanId),
            };
            if (winner.Lease is { } winnerLease)
            {
                signals.Add(new FlowSubject(Id.ToString(), $"{winnerLease.RepositoryId}:{winnerLease.BranchName}"));
            }

            await Save(State with { Waiting = nextWaiting, Inner = nextInner }, signals).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(previousPlanId)
                && !string.Equals(previousPlanId, newPlanId, StringComparison.Ordinal))
            {
                await requests.Withdraw(previousPlanId).ConfigureAwait(true);
            }

            var remainingWindow = expires > now ? expires - now : TimeSpan.FromSeconds(1);
            var remintedPlan = PlanFor("approval-gate", nextInner) with
            {
                Id = newPlanId,
                Expiry = remainingWindow,
            };
            await requests.Request(remintedPlan).ConfigureAwait(true);
            return;
        }

        if (ReferenceEquals(nextInner, currentInner) || nextInner == currentInner)
        {
            return;
        }

        Signal updateSignal = winner.Lease is { } selectedLease
            ? new FlowSubject(Id.ToString(), $"{selectedLease.RepositoryId}:{selectedLease.BranchName}")
            : new FlowStep(Id.ToString(), State.Current ?? "implement-tasks", StepOutcome.Started, string.Create(CultureInfo.InvariantCulture, $"Selected candidate w{candidateIndex + 1}."));

        await Save(State with { Inner = nextInner }, updateSignal).ConfigureAwait(true);
    }

    protected override async Task<StepResult<EngineerRun>> Run(string step, EngineerRun inner, CancellationToken ct)
    {
        try
        {
            return step switch
            {
                "brainstorm-and-plan" => await BrainstormAndPlanStepAsync(inner, ct).ConfigureAwait(true),
                "prepare-worktree" => await PrepareWorktreeStepAsync(inner, ct).ConfigureAwait(true),
                "implement-tasks" => await ImplementTasksStepAsync(inner, ct).ConfigureAwait(true),
                "build-and-test" => await BuildAndTestStepAsync(inner, ct).ConfigureAwait(true),
                "live-verify" => await LiveVerifyStepAsync(inner, ct).ConfigureAwait(true),
                "approval-gate" => await ApprovalGateStepAsync(step, inner).ConfigureAwait(true),
                "push" => await PushStepAsync(inner, ct).ConfigureAwait(true),
                "pr" => await PullRequestStepAsync(inner, ct).ConfigureAwait(true),
                "clean" => await CleanStepAsync(inner, ct).ConfigureAwait(true),
                _ => throw new FlowFailed(step, $"Unknown EngineerFlow step '{step}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Context.Interrupts(ex))
        {
            var activeLeases = inner.Instances
                .Select(static i => i.Lease)
                .OfType<WorkTreeLease>()
                .ToArray();
            await EngineerPipelineStages.CleanupFailedLeasesBestEffortAsync(
                WorkTree,
                activeLeases,
                inner.Publication?.Cleaned == true,
                ConsoleLogger).ConfigureAwait(true);

            if (ex is FlowFailed)
            {
                throw;
            }

            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, EngineerRun inner)
    {
        var winner = inner.SelectedInstance
            ?? throw new InvalidOperationException("Cannot raise Engineer confirmation plan without a verified candidate instance.");
        var verifiedCommit = winner.Commit?.Commit
            ?? throw new InvalidOperationException("Cannot raise Engineer confirmation plan without a verified CommitSha.");
        var planId = inner.Approval?.PlanId is { Length: > 0 } existing && existing.EndsWith(":" + verifiedCommit.Value, StringComparison.Ordinal)
            ? existing
            : BuildPlanId(EffectiveRunId(inner), verifiedCommit.Value);

        var plan = EngineerPipelineStages.BuildConfirmationPlan(
            planId,
            ConfirmationKind,
            "Discard Worktrees",
            ApprovalWindow,
            inner.ToSelectedChange(),
            verifiedCommit);

        // The person who approves reads first what was not checked.
        return inner.Verification.LeavesUnchecked ? plan with { Body = inner.Verification.Describe() + "\n\n" + plan.Body } : plan;
    }

    // A gate the run's policy switches off is done as a skipped step: journaled with the reason, never run.
    private static StepResult<EngineerRun>? SkippedByPolicy(string gate, EngineerRun inner)
        => inner.Verification.Runs(gate)
            ? null
            : new StepResult<EngineerRun>(
                inner,
                Disposition: StepDisposition.Skip,
                Reason: $"{gate} switched off by {inner.Verification.SkippedBy ?? "the starter"} when the run was started; this gate did not run.");

    public static string BuildPlanId(string runId, string commitSha)
        => EngineerPipelineStages.BuildPlanId(PlanPrefix, runId, commitSha);

    private async Task<StepResult<EngineerRun>> BrainstormAndPlanStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Repository is null)
        {
            throw new FlowFailed("brainstorm-and-plan", "Repository id is required.");
        }

        if (string.IsNullOrWhiteSpace(inner.Goal))
        {
            throw new FlowFailed("brainstorm-and-plan", "Goal description is required.");
        }

        var policy = ResolvePolicy(inner.Repository.Value);
        if (inner.Plan is not null && inner.RatifiedPlan is { Ratified: true } && inner.Plan.Validate(policy.MaxChangedFiles) is null)
        {
            return inner.Advance();
        }

        EngineerTaskPlan plan;
        if (inner.Plan is not null && inner.Plan.Validate(policy.MaxChangedFiles) is null)
        {
            plan = inner.Plan;
        }
        else
        {
            var activePlanner = Planner;
            var allowed = policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (activePlanner is EngineerPlanner stagePlanner)
            {
                var wm = ResolveWorkingMemoryOnGrainTurn();
                var preHydratedMemory = await stagePlanner.HydrateMemoryBriefAsync(
                    inner.Repository.Value,
                    inner.Goal,
                    Id.Brain,
                    wm,
                    ct).ConfigureAwait(true);
                var optionalWt = workTree ?? services?.GetService(typeof(IWorkTree)) as IWorkTree;
                var activeRoslyn = Roslyn;
                var brainId = Id.Brain;
                plan = await Task.Run(
                    () => stagePlanner.PlanWithContextAsync(
                        inner.Repository.Value,
                        inner.Goal,
                        allowed,
                        policy.MaxChangedFiles,
                        brainId,
                        optionalWt,
                        activeRoslyn,
                        workingMemoryOverride: null,
                        preHydratedMemory: preHydratedMemory,
                        repositoryPathOverride: null,
                        ct),
                    ct).ConfigureAwait(true);
            }
            else
            {
                plan = await Task.Run(
                    () => activePlanner.PlanAsync(inner.Repository.Value, inner.Goal, allowed, policy.MaxChangedFiles, ct),
                    ct).ConfigureAwait(true);
            }
        }

        var slices = inner.RequestedSlices is { Length: > 0 }
            ? inner.RequestedSlices
            : plan.Slices is { Length: > 0 }
                ? plan.Slices
                : EngineerTaskSlices.FromTaskPlan(plan);
        var cleanupTargets = MergeCleanupTargets(inner.RequestedCleanupTargets, plan.CleanupTargets, slices, policy.MaxChangedFiles);

        var disjointRule = new NoOverlappingFileSlices(slices);
        if (disjointRule.Broken)
        {
            throw new FlowFailed("brainstorm-and-plan", $"{disjointRule.Name}: {disjointRule.Message}");
        }

        var enrichedPlan = plan with
        {
            CleanupTargets = cleanupTargets,
            Slices = slices,
        };
        if (enrichedPlan.Validate(policy.MaxChangedFiles) is { } validationError)
        {
            throw new FlowFailed("brainstorm-and-plan", validationError);
        }

        if (inner.RequirePlanRatification && inner.RatifiedPlan is not { Ratified: true })
        {
            var draft = new RatifiedPlan(
                Goal: inner.Goal,
                ArchitectureSummary: enrichedPlan.ArchitectureSummary,
                CleanupTargets: cleanupTargets,
                Slices: slices,
                Ratified: false,
                RatifiedBy: null,
                RatifiedAt: null,
                Harness: inner.Harness,
                CodeGraphDigest: enrichedPlan.CodeGraphDigest);
            var waitingInner = inner.WithPlan(enrichedPlan).WithDraftPlan(draft);
            var ratifyWaitId = $"plan:ratify:{EffectiveRunId(waitingInner)}";
            return new StepResult<EngineerRun>(
                waitingInner,
                new Waiting("brainstorm-and-plan", WaitReason.External, null, ratifyWaitId),
                Disposition: StepDisposition.Wait);
        }

        var autoRatified = new RatifiedPlan(
            Goal: inner.Goal,
            ArchitectureSummary: enrichedPlan.ArchitectureSummary,
            CleanupTargets: cleanupTargets,
            Slices: slices,
            Ratified: true,
            RatifiedBy: "auto",
            RatifiedAt: Context.Time.GetUtcNow(),
            Harness: inner.Harness,
            CodeGraphDigest: enrichedPlan.CodeGraphDigest);

        return inner.WithPlan(enrichedPlan).WithRatifiedPlan(autoRatified).Advance();
    }

    private async Task<StepResult<EngineerRun>> PrepareWorktreeStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Plan is null)
        {
            throw new FlowFailed("prepare-worktree", "EngineerTaskPlan is required before preparing worktrees.");
        }

        var runId = EffectiveRunId(inner);
        var existingLeases = inner.Instances.Select(static i => i.Lease).OfType<WorkTreeLease>().ToArray();
        var (leases, subject) = await EngineerPipelineStages.PrepareWorktreesAsync(
            WorkTree,
            inner.Repository,
            inner.Goal,
            runId,
            inner.InstanceCount,
            inner.RequestedBaseBranch,
            existingLeases,
            Id.ToString(),
            "prepare-worktree",
            ct,
            inner.RequestedBackend).ConfigureAwait(true);

        return inner.WithLeases(runId, leases).Advance(subject);
    }

    private async Task<StepResult<EngineerRun>> ImplementTasksStepAsync(EngineerRun inner, CancellationToken ct)
    {
        var plan = inner.Plan ?? throw new FlowFailed("implement-tasks", "EngineerTaskPlan is missing.");
        if (inner.Instances.Length == 0)
        {
            throw new FlowFailed("implement-tasks", "No prepared worktree instances available.");
        }

        var ratifiedPlan = inner.RatifiedPlan ?? new RatifiedPlan(
            Goal: inner.Goal,
            ArchitectureSummary: plan.ArchitectureSummary,
            CleanupTargets: plan.CleanupTargets ?? [],
            Slices: EngineerTaskSlices.FromTaskPlan(plan),
            Ratified: true,
            RatifiedBy: "auto",
            RatifiedAt: Context.Time.GetUtcNow(),
            Harness: inner.Harness);

        var disjointRule = new NoOverlappingFileSlices(ratifiedPlan.Slices);
        if (disjointRule.Broken)
        {
            throw new FlowFailed("implement-tasks", $"{disjointRule.Name}: {disjointRule.Message}");
        }

        // Pre-Flight Brain Hydration ONCE per mission on the grain turn, shared across all worktrees & slices!
        var hydratedSnapshot = await HydrateMissionBrainSnapshotAsync(
            inner.Repository?.Value ?? "os",
            inner.Goal,
            ct).ConfigureAwait(true);

        var wt = WorkTree;
        var activeRoslyn = Roslyn;
        var harness = ResolveHarness(inner.Harness, inner.Repository?.Value);
        var totalInstances = inner.Instances.Length;
        var flowId = Id.ToString();
        var brainId = Id.Brain;
        var grainScheduler = TaskScheduler.Current;

        Task PublishProgressLive(OperationProgressed signal) =>
            Task.Factory.StartNew(
                async () =>
                {
                    try
                    {
                        await Publish(signal).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ConsoleLogger.LogDebug(ex, "Skipping live OperationProgressed publish on stopping grain.");
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                grainScheduler).Unwrap();

        var instanceRuns = inner.Instances.Select(instance => Task.Run(
            () => ImplementSingleInstanceAsync(
                instance,
                brainId,
                flowId,
                inner.Repository,
                EffectiveRunId(inner),
                inner.Goal,
                inner.RequestedBaseBranch,
                inner.Harness,
                totalInstances,
                ratifiedPlan,
                hydratedSnapshot,
                wt,
                harness,
                activeRoslyn,
                PublishProgressLive,
                ct),
            ct)).ToArray();

        var completedInstances = await Task.WhenAll(instanceRuns).ConfigureAwait(true);
        if (completedInstances.All(static i => i.Commit is null || string.Equals(i.Status, "failed", StringComparison.Ordinal)))
        {
            var reason = completedInstances.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed during implement-tasks.";
            throw new FlowFailed("implement-tasks", reason);
        }

        var nextSelected = EngineerRun.SelectDefaultWinnerIndex(completedInstances);
        var updated = inner
            .WithMatchedProjectBrief(hydratedSnapshot.MatchedProjectBriefKey)
            .WithUpdatedInstances(completedInstances, nextSelected);

        if (updated.SelectedInstance?.Commit is { } selectedCommit)
        {
            EngineerPipelineStages.EnsureCardAcceptsDiff(
                updated.ToSelectedChange,
                selectedCommit,
                "implement-tasks");
        }

        return updated.Advance();
    }

    private static async Task<EngineerInstance> ImplementSingleInstanceAsync(
        EngineerInstance instance,
        BrainId brainId,
        string flowId,
        RepositoryId? repository,
        string runId,
        string goal,
        BranchName? requestedBaseBranch,
        EngineeringHarnessKind harnessKind,
        int totalInstances,
        RatifiedPlan ratifiedPlan,
        InoHydratedBrainSnapshot hydratedSnapshot,
        IWorkTree wt,
        IEngineeringHarness harness,
        IWorktreeRoslyn activeRoslyn,
        Func<OperationProgressed, Task> onProgress,
        CancellationToken ct)
    {
        var lease = instance.Lease ?? throw new InvalidOperationException($"WorkTreeLease missing on instance {instance.InstanceIndex}.");
        await WorkTreeService.VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(true);

        if (instance.Commit is not null)
        {
            return instance.WithComputedScore();
        }

        var (strategyId, strategyLabel, strategyLens) = EngineerCandidateStrategies.ForCandidate(instance.InstanceIndex);
        var effectiveStrategy = string.IsNullOrWhiteSpace(instance.Strategy) ? strategyId : instance.Strategy!;

        try
        {
            var request = new EngineeringHarnessRequest(
                FlowId: flowId,
                RunId: runId,
                RepositoryId: repository?.Value ?? lease.RepositoryId,
                Goal: goal,
                WorktreeId: lease.EffectiveLeaseId,
                WorktreePath: lease.TreePath,
                InstanceIndex: instance.InstanceIndex,
                TotalInstances: totalInstances,
                Strategy: effectiveStrategy,
                StrategyLabel: strategyLabel,
                StrategyPromptLens: strategyLens,
                Harness: harnessKind,
                RatifiedPlan: ratifiedPlan,
                Brain: brainId,
                BaseBranch: lease.BaseBranch,
                SourceRepositoryPath: lease.SourceRepoPath);

            var harnessResult = harness is InoNativeHarness inoNative
                ? await inoNative.ExecuteWithContextAsync(
                    request,
                    preHydrated: hydratedSnapshot,
                    recordJournalMilestone: false,
                    onProgress: onProgress,
                    ct: ct).ConfigureAwait(true)
                : await harness.ExecuteAsync(request, onProgress, ct).ConfigureAwait(true);

            var completedIds = harnessResult.CompletedSlices
                .Where(static s => string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase))
                .Select(static s => s.SliceId)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (!harnessResult.Succeeded)
            {
                return (instance with
                {
                    Harness = harnessKind,
                    Strategy = effectiveStrategy,
                    CompletedTaskIds = completedIds,
                    Slices = harnessResult.CompletedSlices,
                    DeletedFiles = harnessResult.DeletedFiles,
                    Status = "failed",
                    FailureDetail = harnessResult.FailureDetail ?? "Engineering harness execution failed.",
                }).WithComputedScore();
            }

            var touchedRepoPaths = harnessResult.TouchedFiles
                .Select(static raw => RepoPath.TryParse(raw, out var parsed) ? parsed : null)
                .OfType<RepoPath>()
                .Distinct()
                .OrderBy(static p => p.Value, StringComparer.Ordinal)
                .ToArray();

            var liveCsPaths = touchedRepoPaths
                .Where(p => p.IsCSharp && !(harnessResult.DeletedFiles ?? []).Contains(p.Value, StringComparer.OrdinalIgnoreCase))
                .Select(static p => p.Value)
                .ToArray();
            var roslynDiagnostics = liveCsPaths.Length > 0
                ? (await activeRoslyn.AnalyzeAsync(lease.TreePath, liveCsPaths, ct).ConfigureAwait(true)).ToArray()
                : [];

            var commitMessage = ChangePolicy.BuildCommitMessage(goal, harnessResult.Summary);
            var committed = await wt.VerifyAndCommitAsync(lease, commitMessage, ct).ConfigureAwait(true);

            if (repository is not null)
            {
                var metadataWithoutDiff = ChangeSet.Create(
                    committed.Commit,
                    committed.ChangedFiles,
                    string.Empty,
                    committed.Additions,
                    committed.Deletions);
                var previewChange = Change.Start(runId, repository, goal, requestedBaseBranch)
                    .WithWorkTree(string.IsNullOrWhiteSpace(runId) ? lease.RunId : runId, lease)
                    .WithCoderEdit(new CoderEditResult(
                        harnessResult.Summary,
                        harnessResult.TouchedFiles,
                        harnessResult.ModelId))
                    .WithCommit(metadataWithoutDiff);
                if (ChangeCard.Refusal(previewChange, committed.HostRenderedDiff) is { } cardRefusal)
                {
                    return (instance with
                    {
                        Harness = harnessKind,
                        Strategy = effectiveStrategy,
                        CompletedTaskIds = completedIds,
                        Slices = harnessResult.CompletedSlices,
                        DeletedFiles = harnessResult.DeletedFiles,
                        RoslynDiagnostics = roslynDiagnostics,
                        Status = "failed",
                        FailureDetail = cardRefusal,
                    }).WithComputedScore();
                }
            }

            return (instance with
            {
                Harness = harnessKind,
                Strategy = effectiveStrategy,
                CompletedTaskIds = completedIds,
                Slices = harnessResult.CompletedSlices,
                DeletedFiles = harnessResult.DeletedFiles,
                Edit = new CoderEdit(
                    harnessResult.Summary,
                    harnessResult.ModelId,
                    touchedRepoPaths),
                RoslynDiagnostics = roslynDiagnostics,
                Commit = committed,
                Status = "implemented",
                FailureDetail = null,
            }).WithComputedScore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLogger.LogSwallowed(ex, $"engineer worktree instance #{instance.InstanceIndex} implementation failed");
            return (instance with
            {
                Harness = harnessKind,
                Strategy = effectiveStrategy,
                Status = "failed",
                FailureDetail = ex.Message,
            }).WithComputedScore();
        }
    }

    private ChangePolicy ResolvePolicy(string? repositoryId)
        => services?.GetService<ChangePolicy>() ?? ChangePolicy.ForRepository(repositoryId);

    private IEngineeringHarness ResolveHarness(EngineeringHarnessKind kind, string? repositoryId)
    {
        if (engineeringHarness is not null && engineeringHarness.Kind == kind)
        {
            return engineeringHarness;
        }

        if (services?.GetService(typeof(IEngineeringHarness)) is IEngineeringHarness registered && registered.Kind == kind)
        {
            return registered;
        }

        var policy = ResolvePolicy(repositoryId);
        if (kind is EngineeringHarnessKind.ClaudeCli or EngineeringHarnessKind.CodexCli)
        {
            return new ExternalCliHarness(
                kind,
                sandboxRunner: services?.GetService<ISandboxCommandRunner>(),
                roslyn: Roslyn,
                policy: policy,
                configuration: services?.GetService<IConfiguration>());
        }

        return InoNativeHarness.CreateDefault(
            services: services,
            coder: OptionalCoder,
            roslyn: Roslyn,
            policy: policy);
    }

    private static string[] MergeCleanupTargets(
        string[]? requestedCleanup,
        string[]? plannedCleanup,
        IReadOnlyList<EngineerTaskSlice> slices,
        int maxChangedFiles)
    {
        if (requestedCleanup is { Length: > 0 })
        {
            if (plannedCleanup is not { Length: > 0 })
            {
                return requestedCleanup;
            }

            var sliceDistinctCount = slices
                .SelectMany(static s => s.OwnedPaths ?? [])
                .Select(EngineerTaskSlices.NormalizePath)
                .Where(static p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var merged = new List<string>(requestedCleanup);
            foreach (var candidate in plannedCleanup)
            {
                if (merged.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sliceDistinctCount + merged.Count >= maxChangedFiles)
                {
                    break;
                }

                merged.Add(candidate);
            }

            return [.. merged];
        }

        return plannedCleanup ?? [];
    }

    private IWorkingMemory? ResolveWorkingMemoryOnGrainTurn()
    {
        IWorkingMemory? wm = services?.GetService<IWorkingMemory>();
        if (wm is null)
        {
            var wmId = IWorkingMemory.NeuronIdOf(Id.Brain);
            if (Implements<IWorkingMemory>(wmId))
            {
                wm = Context.Get<IWorkingMemory>(wmId);
            }
        }

        return wm;
    }

    private async Task<InoHydratedBrainSnapshot> HydrateMissionBrainSnapshotAsync(
        string repositoryId,
        string goal,
        CancellationToken ct)
    {
        var wm = ResolveWorkingMemoryOnGrainTurn();

        IUiIntrospector? introspector = services?.GetService<IUiIntrospector>();
        if (introspector is null)
        {
            var uiId = IUiIntrospector.NeuronIdOf(Id.Brain);
            if (Implements<IUiIntrospector>(uiId))
            {
                introspector = Context.Get<IUiIntrospector>(uiId);
            }
        }

        IKnowledgeStore? ks = services?.GetService<IKnowledgeStore>();
        if (ks is null)
        {
            var ksId = new NeuronId(Id.Brain, "ai.knowledge-store", InoNativeHarness.KnowledgeStoreKey);
            if (Implements<IKnowledgeStore>(ksId))
            {
                ks = Context.Get<IKnowledgeStore>(ksId);
            }
        }

        return await InoNativeHarness.HydrateBrainContextAsync(
            Id.Brain,
            repositoryId,
            goal,
            wm,
            introspector,
            ks,
            ConsoleLogger,
            ct).ConfigureAwait(true);
    }

    private async Task TryRecordSessionMilestoneOnGrainTurnAsync(
        EngineerRun inner,
        string[] deliverables,
        string[] decisions,
        string[]? openThreads,
        string[]? resolvedThreads)
    {
        ISessionJournal? journal = services?.GetService<ISessionJournal>();
        if (journal is null)
        {
            var journalId = ISessionJournal.NeuronIdOf(Id.Brain);
            if (Implements<ISessionJournal>(journalId))
            {
                journal = Context.Get<ISessionJournal>(journalId);
            }
        }

        if (journal is null)
        {
            return;
        }

        await InoNativeHarness.RecordMilestoneBestEffortAsync(
            journal,
            Id.ToString(),
            inner.Goal,
            inner.MatchedProjectBriefKey,
            deliverables,
            decisions,
            openThreads,
            resolvedThreads,
            ConsoleLogger).ConfigureAwait(true);
    }

    private async Task<StepResult<EngineerRun>> BuildAndTestStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (SkippedByPolicy(EngineerVerificationPolicy.BuildAndTestGate, inner) is { } skipped)
        {
            return skipped;
        }

        var verifier = BuildVerifier;
        var wt = WorkTree;
        var activeRoslyn = Roslyn;
        var harness = ResolveHarness(inner.Harness, inner.Repository?.Value);
        var hydratedSnapshot = await HydrateMissionBrainSnapshotAsync(
            inner.Repository?.Value ?? "os",
            inner.Goal,
            ct).ConfigureAwait(true);
        var runId = EffectiveRunId(inner);
        var flowId = Id.ToString();
        var brainId = Id.Brain;
        var totalInstances = inner.Instances.Length;
        var baseRatifiedPlan = inner.RatifiedPlan ?? (inner.Plan is { } plan
            ? new RatifiedPlan(
                Goal: inner.Goal,
                ArchitectureSummary: plan.ArchitectureSummary,
                CleanupTargets: [],
                Slices: EngineerTaskSlices.FromTaskPlan(plan),
                Ratified: true,
                RatifiedBy: "auto",
                RatifiedAt: Context.Time.GetUtcNow(),
                Harness: inner.Harness)
            : new RatifiedPlan(
                Goal: inner.Goal,
                ArchitectureSummary: string.Empty,
                CleanupTargets: [],
                Slices: [],
                Ratified: true,
                RatifiedBy: "auto",
                RatifiedAt: Context.Time.GetUtcNow(),
                Harness: inner.Harness));
        var grainScheduler = TaskScheduler.Current;

        Task PublishProgressLive(OperationProgressed signal) =>
            Task.Factory.StartNew(
                async () =>
                {
                    try
                    {
                        await Publish(signal).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ConsoleLogger.LogDebug(ex, "Skipping live OperationProgressed publish on stopping grain.");
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                grainScheduler).Unwrap();

        var tasks = inner.Instances.Select(instance => Task.Run(
            () => BuildAndRepairInstanceAsync(
                instance,
                brainId,
                flowId,
                inner.Repository,
                runId,
                inner.Goal,
                inner.Harness,
                totalInstances,
                baseRatifiedPlan,
                hydratedSnapshot,
                wt,
                harness,
                activeRoslyn,
                verifier,
                PublishProgressLive,
                ct),
            ct)).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);
        var passing = results.Where(static i => i.Build is { Succeeded: true } && i.Commit is not null).ToArray();
        if (passing.Length == 0)
        {
            var detail = results.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed build-and-test verification.";
            throw new FlowFailed("build-and-test", detail);
        }

        var nextWinner = EngineerRun.SelectDefaultWinnerIndex(results);
        return inner.WithUpdatedInstances(results, nextWinner).Advance();
    }

    private static async Task<EngineerInstance> BuildAndRepairInstanceAsync(
        EngineerInstance instance,
        BrainId brainId,
        string flowId,
        RepositoryId? repository,
        string runId,
        string goal,
        EngineeringHarnessKind harnessKind,
        int totalInstances,
        RatifiedPlan baseRatifiedPlan,
        InoHydratedBrainSnapshot hydratedSnapshot,
        IWorkTree wt,
        IEngineeringHarness harness,
        IWorktreeRoslyn activeRoslyn,
        IEngineerBuildVerifier verifier,
        Func<OperationProgressed, Task> onProgress,
        CancellationToken ct)
    {
        if (instance.Commit is null || instance.Lease is null)
        {
            return instance.WithComputedScore();
        }

        if (instance.Build is { Succeeded: true })
        {
            return instance.WithComputedScore();
        }

        var current = instance;
        var lease = instance.Lease;
        var currentCommit = instance.Commit;
        var changedPaths = currentCommit.ChangedFiles.Select(static f => f.Value).ToArray();
        var report = await VerifyInstanceBuildAsync(verifier, lease, changedPaths, ct).ConfigureAwait(true);

        var (strategyId, strategyLabel, strategyLens) = EngineerCandidateStrategies.ForCandidate(instance.InstanceIndex);
        var effectiveStrategy = string.IsNullOrWhiteSpace(instance.Strategy) ? strategyId : instance.Strategy!;
        var ownedPaths = (baseRatifiedPlan.Slices ?? [])
            .SelectMany(static s => s.OwnedPaths ?? [])
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var repairRound = 0;
        while (!report.Succeeded && repairRound < MaxBuildRepairRounds)
        {
            ct.ThrowIfCancellationRequested();
            repairRound++;

            var repairGoal = BuildRepairGoalPrompt(goal, repairRound, report);
            var repairSlice = new EngineerTaskSlice(
                SliceId: string.Create(CultureInfo.InvariantCulture, $"build-repair-r{repairRound}"),
                Title: string.Create(CultureInfo.InvariantCulture, $"Build & test repair round {repairRound}/{MaxBuildRepairRounds}"),
                OwnedPaths: ownedPaths,
                DependsOnSliceIds: [],
                Status: "pending",
                Percent: 0,
                AssignedAgentKey: string.Create(CultureInfo.InvariantCulture, $"ino.repair-r{repairRound}"),
                Language: "csharp",
                Instructions: repairGoal);
            var repairPlan = baseRatifiedPlan with
            {
                Goal = repairGoal,
                CleanupTargets = [],
                Slices = [repairSlice],
            };

            var repairRequest = new EngineeringHarnessRequest(
                FlowId: flowId,
                RunId: runId,
                RepositoryId: repository?.Value ?? lease.RepositoryId,
                Goal: repairGoal,
                WorktreeId: lease.EffectiveLeaseId,
                WorktreePath: lease.TreePath,
                InstanceIndex: instance.InstanceIndex,
                TotalInstances: totalInstances,
                Strategy: effectiveStrategy,
                StrategyLabel: strategyLabel,
                StrategyPromptLens: strategyLens,
                Harness: harnessKind,
                RatifiedPlan: repairPlan,
                Brain: brainId,
                BaseBranch: lease.BaseBranch,
                SourceRepositoryPath: lease.SourceRepoPath);

            try
            {
                var harnessResult = harness is InoNativeHarness inoNative
                    ? await inoNative.ExecuteWithContextAsync(
                        repairRequest,
                        preHydrated: hydratedSnapshot,
                        recordJournalMilestone: false,
                        onProgress: onProgress,
                        ct: ct).ConfigureAwait(true)
                    : await harness.ExecuteAsync(repairRequest, onProgress, ct).ConfigureAwait(true);

                if (harnessResult.Succeeded)
                {
                    var touchedRepoPaths = harnessResult.TouchedFiles
                        .Select(static raw => RepoPath.TryParse(raw, out var parsed) ? parsed : null)
                        .OfType<RepoPath>()
                        .Distinct()
                        .OrderBy(static p => p.Value, StringComparer.Ordinal)
                        .ToArray();

                    var liveCsPaths = touchedRepoPaths
                        .Where(p => p.IsCSharp && !(current.DeletedFiles ?? []).Contains(p.Value, StringComparer.OrdinalIgnoreCase))
                        .Select(static p => p.Value)
                        .ToArray();
                    var roslynDiagnostics = liveCsPaths.Length > 0
                        ? (await activeRoslyn.AnalyzeAsync(lease.TreePath, liveCsPaths, ct).ConfigureAwait(true)).ToArray()
                        : current.RoslynDiagnostics ?? [];

                    var commitMessage = ChangePolicy.BuildCommitMessage(goal, harnessResult.Summary);
                    currentCommit = await wt.VerifyAndAmendAsync(lease, currentCommit.Commit, commitMessage, ct).ConfigureAwait(true);
                    changedPaths = currentCommit.ChangedFiles.Select(static f => f.Value).ToArray();

                    current = current with
                    {
                        Edit = new CoderEdit(
                            harnessResult.Summary,
                            harnessResult.ModelId,
                            touchedRepoPaths),
                        RoslynDiagnostics = roslynDiagnostics,
                        Commit = currentCommit,
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLogger.LogSwallowed(
                    ex,
                    $"engineer worktree instance #{instance.InstanceIndex} build repair round {repairRound}/{MaxBuildRepairRounds} failed");
            }

            report = await VerifyInstanceBuildAsync(verifier, lease, changedPaths, ct).ConfigureAwait(true);
        }

        return (current with
        {
            Build = report,
            Status = report.Succeeded ? "built" : "failed",
            FailureDetail = report.Succeeded ? null : report.Summary,
        }).WithComputedScore();
    }

    private static Task<EngineerBuildReport> VerifyInstanceBuildAsync(
        IEngineerBuildVerifier verifier,
        WorkTreeLease lease,
        string[] changedPaths,
        CancellationToken ct)
        => verifier is EngineerBuildVerifier stageVerifier
            ? stageVerifier.VerifyAsync(lease, changedPaths, ct)
            : verifier.VerifyAsync(lease.TreePath, changedPaths, ct);

    private static string BuildRepairGoalPrompt(string originalGoal, int repairRound, EngineerBuildReport report)
    {
        var diagnosticsBlock = report.Diagnostics is { Length: > 0 }
            ? string.Join("\n", report.Diagnostics.Take(25).Select(static d => $"- {d}"))
            : $"- {report.Summary}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{originalGoal}\n\n[Build & Test Repair — Round {repairRound}/{MaxBuildRepairRounds}]\nPrevious build/test verification failed: {report.Summary}\nDiagnostics:\n{diagnosticsBlock}\nFix all reported compiler errors and failing tests inside the worktree.");
    }

    private async Task<StepResult<EngineerRun>> LiveVerifyStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (SkippedByPolicy(EngineerVerificationPolicy.LiveVerifyGate, inner) is { } skipped)
        {
            return skipped;
        }

        var verifier = LiveVerifier;
        if (verifier is ILiveEnvironment { Configured: false })
        {
            // Nothing is configured to check against on this installation. The run records that, which is neither a pass nor a
            // person's skip, and goes on to the approval, where the person reads it first. The verifier is not called. A run
            // that was parked here before this rule takes this path at its next recheck or resume.
            return new StepResult<EngineerRun>(
                inner.WithLiveVerifyNotAvailable(),
                Disposition: StepDisposition.Skip,
                Reason: LiveVerifyNotAvailableReason);
        }

        var liveContext = BuildLiveVerificationContext();
        var unchecked_ = 0;
        var liveTasks = inner.Instances.Select(async instance =>
        {
            if (!inner.BuildGatePassed(instance) || instance.Commit is null || instance.Lease is null)
            {
                return instance.WithComputedScore();
            }

            if (instance.Live is { Succeeded: true })
            {
                return instance.WithComputedScore();
            }

            var changedPaths = instance.Commit.ChangedFiles.Select(static f => f.Value).ToArray();
            var instanceLiveContext = liveContext with { CommitSha = instance.Commit.Commit.Value, BrainId = Id.Brain.Value };
            var report = verifier is EngineerLiveVerifier stageVerifier
                ? await stageVerifier.VerifyLiveAsync(instance.Lease.TreePath, inner.Goal, changedPaths, instanceLiveContext, ct).ConfigureAwait(true)
                : await Task.Run(
                    () => verifier.VerifyLiveAsync(instance.Lease.TreePath, inner.Goal, changedPaths, ct),
                    ct).ConfigureAwait(true);
            if (EngineerLiveVerifier.EnvironmentMissing(report))
            {
                // Not checked is not failed: the candidate keeps its state and is checked when there is something to check against.
                Interlocked.Increment(ref unchecked_);
                return instance.WithComputedScore();
            }

            return (instance with
            {
                Live = report,
                Status = report.Succeeded ? "verified" : "failed",
                FailureDetail = report.Succeeded ? null : report.Summary,
            }).WithComputedScore();
        }).ToArray();

        var results = await Task.WhenAll(liveTasks).ConfigureAwait(true);
        var verified = results
            .Where(i => i.Live is { Succeeded: true } && inner.BuildGatePassed(i) && i.Commit is not null)
            .ToArray();

        if (verified.Length == 0 && unchecked_ > 0)
        {
            // No live environment: the gate is neither passed nor failed. The run keeps its worktrees and waits here; a resume
            // of this step, or the end of the wait, runs the check again.
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"live-verify waits: there is no live environment to check the change against (the configured one answered that it has no candidate host and no live endpoint). The run checks again in {LiveEnvironmentRecheck.TotalMinutes:0} minutes or when this step is resumed; nothing was skipped.");
            return new StepResult<EngineerRun>(
                inner.WithUpdatedInstances(results, inner.SelectedInstanceIndex),
                Emits: [new FlowStep(Id.ToString(), "live-verify", StepOutcome.Repeated, reason)],
                Disposition: StepDisposition.Repeat,
                Reason: reason,
                Delay: LiveEnvironmentRecheck);
        }

        if (verified.Length == 0)
        {
            var detail = results.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed live behavioral verification.";
            throw new FlowFailed("live-verify", detail);
        }

        var winningCandidateIndex = EngineerRun.SelectDefaultWinnerIndex(results);
        var updated = inner.WithUpdatedInstances(results, winningCandidateIndex);
        if (updated.SelectedInstance?.Lease is { } winningLease)
        {
            return updated.Advance(new FlowSubject(Id.ToString(), $"{winningLease.RepositoryId}:{winningLease.BranchName}"));
        }

        return updated.Advance();
    }

    private EngineerLiveContext BuildLiveVerificationContext()
    {
        IntoChat.OS.Modules.Apps.Trace.ITraceReader? traceReader = null;
        try
        {
            traceReader = Context.Get<IntoChat.OS.Modules.Apps.Trace.ITraceReader>(
                IntoChat.OS.Modules.Apps.Trace.ITraceReader.Key(Id.Brain.Value));
        }
        catch (InvalidOperationException ex)
        {
            ConsoleLogger.LogDebug(ex, "Live verification ITraceReader skipped in detached flow invocation.");
        }

        IReadOnlyList<(string ScreenName, IScreen Screen)>? screens = null;
        try
        {
            screens =
            [
                ("app.tasks", Context.Get<IScreen>(new NeuronId(Id.Brain, "app.tasks", "default"))),
            ];
        }
        catch (InvalidOperationException ex)
        {
            ConsoleLogger.LogDebug(ex, "Live verification IScreen skipped in headless test host.");
        }

        return new EngineerLiveContext(State.TraceId, traceReader, screens);
    }

    private async Task<StepResult<EngineerRun>> ApprovalGateStepAsync(string step, EngineerRun inner)
    {
        var winner = inner.SelectedInstance ?? throw new FlowFailed(step, "No verified candidate instance available.");
        var (planId, expires) = EngineerPipelineStages.PrepareApprovalGate(
            inner.ToSelectedChange(),
            winner.Commit,
            PlanPrefix,
            EffectiveRunId(inner),
            Context.Time.GetUtcNow(),
            ApprovalWindow,
            step);

        var nextInner = inner.WithApprovalPending(planId, expires);
        var changedFiles = winner.Commit?.ChangedFiles.Select(static f => f.Value).ToArray() ?? [];
        var deletedFiles = winner.DeletedFiles ?? [];
        await TryRecordSessionMilestoneOnGrainTurnAsync(
            nextInner,
            deliverables:
            [
                $"Verified commit {winner.Commit?.Commit.Value} on branch {winner.Lease?.BranchName} ({changedFiles.Length} changed file(s))",
                .. (winner.Slices ?? []).Select(static s => $"Slice {s.SliceId} ({s.AssignedAgentKey}): {s.Title}"),
                .. deletedFiles.Select(static d => $"Deleted dead target: {d}"),
            ],
            decisions:
            [
                string.Create(CultureInfo.InvariantCulture, $"Selected candidate w{winner.InstanceIndex + 1} ({winner.EffectiveStrategy}, score={winner.EffectiveScore.TotalScore}) using {inner.Harness}."),
            ],
            openThreads: [$"Human approval pending on confirmation plan {planId}"],
            resolvedThreads: null).ConfigureAwait(true);

        return nextInner.WaitApproval(step, expires);
    }

    private async Task<StepResult<EngineerRun>> PushStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inner.Publication?.PushedRef))
        {
            return inner.Advance();
        }

        var winner = inner.SelectedInstance ?? throw new FlowFailed("push", "Selected worktree instance is missing.");
        var commit = winner.Commit ?? throw new FlowFailed("push", "Verified CommitSha is missing.");
        var planId = inner.Approval?.PlanId ?? BuildPlanId(EffectiveRunId(inner), commit.Commit.Value);
        var nativeRepository = await TryResolveRepositoryAsync(inner, winner.Lease).ConfigureAwait(true);
        var (approvedBy, pushedCommit, pushedRef) = await EngineerPipelineStages.ExecutePushAsync(
            WorkTree,
            ConfirmationRequests,
            winner.Lease,
            commit,
            planId,
            inner.EnsureCanPublish,
            ct,
            repository: nativeRepository).ConfigureAwait(true);

        return inner.WithPushed(approvedBy, pushedCommit, pushedRef).Advance();
    }

    private async Task<StepResult<EngineerRun>> PullRequestStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Publication?.PullRequestCompleted == true)
        {
            return inner.Advance();
        }

        var winner = inner.SelectedInstance ?? throw new FlowFailed("pr", "Selected worktree instance is missing.");
        var nativeRepository = await TryResolveRepositoryAsync(inner, winner.Lease).ConfigureAwait(true);
        var prUrl = await EngineerPipelineStages.ExecutePullRequestAsync(
            WorkTree,
            winner.Lease,
            winner.Commit,
            inner.Approval?.ApprovedBy,
            inner.Approval?.PlanId,
            inner.Goal,
            winner.Edit?.Summary,
            winner.Edit?.ModelId,
            new EngineerPullRequestContext(inner.Plan, inner.Instances.Length, winner, inner.RankCandidates(), inner.Verification),
            ct,
            repository: nativeRepository).ConfigureAwait(true);

        return inner.WithPullRequest(prUrl).Advance();
    }

    // Resolves a connected native GitHub IRepository neuron (backed by short-lived GitHub App installation tokens
    // and REST API) so cloud/headless runs push branches and open Draft PRs without requiring a host `gh` CLI.
    // Returns null cleanly for local runs without a configured GitHub connection so WorkTree fallback remains intact.
    private async Task<IRepository?> TryResolveRepositoryAsync(EngineerRun inner, WorkTreeLease? lease)
    {
        if (repository is not null)
        {
            return repository;
        }

        if (services?.GetService(typeof(IRepository)) is IRepository injected)
        {
            return injected;
        }

        // Local-only / bare-filesystem remotes have no GitHubRepo coordinates; keep them on WorkTree.PushAsync.
        if (string.IsNullOrWhiteSpace(lease?.GitHubRepo))
        {
            return null;
        }

        var candidateKeys = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(inner.Repository?.Value))
        {
            candidateKeys.Add($"connection/github/{inner.Repository.Value.Trim()}");
        }

        var ghRepo = lease.GitHubRepo.Trim();
        candidateKeys.Add($"connection/github/{ghRepo}");
        var slash = ghRepo.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && slash + 1 < ghRepo.Length)
        {
            candidateKeys.Add($"connection/github/{ghRepo[(slash + 1)..]}");
        }

        candidateKeys.Add("connection/github/main");

        foreach (var key in candidateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var neuronId = new NeuronId(Id.Brain, "github", key);
                if (!Implements<IRepository>(neuronId))
                {
                    continue;
                }

                var candidate = Context.Get<IRepository>(neuronId);
                var view = await candidate.Read().ConfigureAwait(true);
                if (!view.Connected)
                {
                    continue;
                }

                // Never push to a mismatched repository connection (especially the shared "main" fallback).
                if (!string.IsNullOrWhiteSpace(view.Repository))
                {
                    if (!string.Equals(view.Repository.Trim(), ghRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else if (string.Equals(key, "connection/github/main", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return candidate;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or NotSupportedException)
            {
                ConsoleLogger.LogDebug(ex, "Skipping unavailable GitHub IRepository candidate '{Key}'.", key);
            }
        }

        return null;
    }

    private async Task<StepResult<EngineerRun>> CleanStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Publication?.Cleaned == true)
        {
            return inner.Advance();
        }

        var activeLeases = inner.Instances.Select(static i => i.Lease).OfType<WorkTreeLease>().ToArray();
        await EngineerPipelineStages.ExecuteCleanupAsync(WorkTree, activeLeases, ct).ConfigureAwait(true);
        var cleaned = inner.WithCleaned();

        var pushedRef = cleaned.Publication?.PushedRef ?? "origin";
        var prPart = !string.IsNullOrWhiteSpace(cleaned.Publication?.PullRequestUrl)
            ? $" · Draft PR {cleaned.Publication!.PullRequestUrl}"
            : string.Empty;
        await TryRecordSessionMilestoneOnGrainTurnAsync(
            cleaned,
            deliverables: [$"Published {pushedRef}{prPart} and cleaned {activeLeases.Length} worktree lease(s)."],
            decisions: [$"Approved by human Person '{cleaned.Approval?.ApprovedBy ?? "owner"}'."],
            openThreads: null,
            resolvedThreads: cleaned.Approval?.PlanId is { Length: > 0 } pid ? [pid] : null).ConfigureAwait(true);

        return cleaned.Advance();
    }

    private string EffectiveRunId(EngineerRun inner)
        => EngineerPipelineStages.ResolveEffectiveRunId(inner.RunId, () => Id.Key);
}
