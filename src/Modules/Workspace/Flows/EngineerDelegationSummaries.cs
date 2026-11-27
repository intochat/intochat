using System.Globalization;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class EngineerDelegationSummaries
{
    public static EngineerRunSummary Failure(
        BrainId brain,
        string repoSlug,
        string goal,
        int parallelInstances,
        string backend,
        string errorMessage,
        string? flowId = null,
        EngineeringHarnessKind harness = EngineeringHarnessKind.InoNative)
    {
        var resolvedFlowId = flowId ?? string.Empty;
        var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(brain, "engineer").Key;
        var tasksKey = IntoChat.OS.Host.Primitives.AppRef.Of(brain, "tasks").Key;
        var engineerUri = string.IsNullOrWhiteSpace(resolvedFlowId)
            ? $"neuron://{appKey}"
            : $"neuron://{appKey}#{resolvedFlowId}";
        var tasksUri = string.IsNullOrWhiteSpace(resolvedFlowId)
            ? $"neuron://{tasksKey}"
            : $"neuron://{tasksKey}#{resolvedFlowId}";

        return new EngineerRunSummary(
            Ok: false,
            FlowId: resolvedFlowId,
            RunId: string.Empty,
            RepoSlug: string.IsNullOrWhiteSpace(repoSlug) ? "os" : repoSlug.Trim(),
            Goal: goal?.Trim() ?? string.Empty,
            Backend: EngineerRun.NormalizeBackend(backend) ?? nameof(WorkTreeBackendKind.LocalGit),
            ParallelInstances: Math.Clamp(parallelInstances <= 0 ? 1 : parallelInstances, 1, EngineerRun.MaxParallelInstances),
            Status: FlowStatus.Failed,
            CurrentStage: "brainstorm-and-plan",
            CompletedStages: [],
            ConfirmationPlanId: null,
            TraceId: null,
            WinningCommitSha: null,
            PushedRef: null,
            PullRequestUrl: null,
            EngineerAppUri: engineerUri,
            TasksBoardUri: tasksUri,
            Plan: null,
            Instances: [],
            DiagnosticsAndTraces: [errorMessage],
            SummaryText: errorMessage,
            Harness: harness);
    }

    public static Task<EngineerRunSummary> BuildAsync(
        BrainId brain,
        string flowId,
        FlowView? view,
        FlowHead head,
        IServiceProvider? services = null,
        CancellationToken ct = default)
        => BuildCoreAsync(brain, flowId, view, head, services, ct);

    private static async Task<EngineerRunSummary> BuildCoreAsync(
        BrainId brain,
        string flowId,
        FlowView? view,
        FlowHead head,
        IServiceProvider? services,
        CancellationToken ct)
    {
        var run = string.IsNullOrWhiteSpace(head.InnerJson)
            ? new EngineerRun()
            : JsonSerializer.Deserialize<EngineerRun>(head.InnerJson, EngineerFlow.Json) ?? new EngineerRun();

        var status = view?.Status ?? head.Status;
        var completedStages = view?.Done is { Length: > 0 } done ? done : head.Done;
        var currentStage = view?.WaitingStep
            ?? view?.Current
            ?? (status == FlowStatus.Completed
                ? "clean"
                : head.Steps.FirstOrDefault(s => !completedStages.Contains(s, StringComparer.Ordinal))
                    ?? completedStages.LastOrDefault()
                    ?? "brainstorm-and-plan");

        var parallelInstances = run.Instances.Length > 0
            ? run.Instances.Length
            : Math.Clamp(run.InstanceCount, 1, EngineerRun.MaxParallelInstances);
        var backend = run.Instances.FirstOrDefault()?.Lease?.Backend.ToString()
            ?? run.RequestedBackend
            ?? nameof(WorkTreeBackendKind.LocalGit);
        var repoSlug = run.Repository?.Value ?? "os";
        var goal = !string.IsNullOrWhiteSpace(run.Goal) ? run.Goal : head.Title;
        var runId = !string.IsNullOrWhiteSpace(run.RunId)
            ? run.RunId
            : NeuronId.TryParse(flowId, out var parsedFlowId) ? parsedFlowId.Key : flowId;

        var awaitingRatification = status == FlowStatus.Waiting
            && string.Equals(currentStage, "brainstorm-and-plan", StringComparison.Ordinal)
            && run.RequirePlanRatification
            && run.RatifiedPlan is not { Ratified: true };

        var winner = run.SelectedInstance;
        var winningSha = winner?.Commit?.Commit.Value;
        var confirmationPlanId = awaitingRatification
            ? view?.ConfirmationPlanId
            : status == FlowStatus.Waiting
                ? (run.Approval?.PlanId ?? view?.ConfirmationPlanId)
                : (view?.ConfirmationPlanId ?? run.Approval?.PlanId);
        var traceId = !string.IsNullOrWhiteSpace(view?.TraceId) ? view!.TraceId : head.TraceId;

        var engineerUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "engineer").Key}#{flowId}";
        var tasksUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "tasks").Key}#{flowId}";

        var candidates = run.RankCandidates();
        var candidateByIndex = candidates.ToDictionary(static c => c.InstanceIndex);
        var instanceSummaries = run.Instances
            .Select(inst =>
            {
                var lease = inst.Lease;
                var changed = inst.Commit?.ChangedFiles.Select(static f => f.Value).ToArray()
                    ?? inst.Edit?.TouchedFiles.Select(static f => f.Value).ToArray()
                    ?? [];
                var ranked = candidateByIndex.GetValueOrDefault(inst.InstanceIndex);
                var score = ranked?.Score ?? inst.EffectiveScore;
                return new EngineerInstanceSummary(
                    InstanceIndex: inst.InstanceIndex,
                    LeaseId: lease?.EffectiveLeaseId ?? string.Create(CultureInfo.InvariantCulture, $"wt-{inst.InstanceIndex}"),
                    Backend: lease?.Backend.ToString() ?? nameof(WorkTreeBackendKind.LocalGit),
                    BranchName: lease?.BranchName ?? string.Create(CultureInfo.InvariantCulture, $"ino/engineer-w{inst.InstanceIndex + 1}"),
                    Status: inst.Status,
                    CompletedTaskIds: inst.CompletedTaskIds ?? [],
                    ChangedFiles: changed,
                    CommitSha: inst.Commit?.Commit.Value,
                    BuildSucceeded: inst.Build?.Succeeded == true,
                    LiveSucceeded: inst.Live?.Succeeded == true,
                    FailureDetail: inst.FailureDetail,
                    Strategy: inst.EffectiveStrategy,
                    TotalScore: score.TotalScore,
                    ChangedLines: score.ChangedLines,
                    UnifiedDiff: score.UnifiedDiff,
                    Rank: ranked?.Rank ?? (inst.InstanceIndex + 1),
                    IsSelected: inst.InstanceIndex == run.SelectedCandidateIndex,
                    Harness: inst.Harness,
                    Slices: inst.Slices,
                    DeletedFiles: inst.DeletedFiles);
            })
            .ToArray();

        var effectiveCleanup = run.RatifiedPlan?.CleanupTargets
            ?? run.Plan?.CleanupTargets
            ?? run.RequestedCleanupTargets
            ?? [];
        var effectiveSlices = winner?.Slices
            ?? run.RatifiedPlan?.Slices
            ?? (run.Plan is not null ? EngineerTaskSlices.FromTaskPlan(run.Plan) : []);

        var collaborators = await MissionCollaboratorResolver.ResolveAsync(
            brain,
            run,
            status,
            currentStage,
            head.Owner,
            services,
            ct).ConfigureAwait(false);

        var traces = CollectDiagnosticsAndTraces(run, winner, candidates, effectiveSlices, effectiveCleanup, view);
        var (ok, summaryText, card) = FormatSummaryAndCard(
            brain,
            flowId,
            head,
            run,
            winner,
            status,
            currentStage,
            completedStages,
            repoSlug,
            backend,
            parallelInstances,
            winningSha,
            confirmationPlanId,
            awaitingRatification,
            engineerUri,
            view?.Detail);

        // A gate that did not check the commit (switched off, or live-verify not available) is said wherever the run's status
        // is: the text the model gets, the card and the diagnostics.
        if (run.Verification.LeavesUnchecked)
        {
            var skippedLine = run.Verification.Describe();
            summaryText = summaryText + "\n" + skippedLine;
            card = card with { Summary = card.Summary + " " + skippedLine };
            traces = [skippedLine, .. traces];
        }

        var candidateHostKind = services?.GetService(typeof(IEngineerCandidateHostVerifier)) is IEngineerCandidateHostVerifier hostVerifier
            && hostVerifier.IsConfigured
                ? hostVerifier.HostKind
                : null;
        var receipt = !string.IsNullOrWhiteSpace(winningSha)
            ? new EngineerVerificationReceipt(
                CommitSha: winningSha,
                PullRequestUrl: run.Publication?.PullRequestUrl,
                PushedRef: run.Publication?.PushedRef,
                BuildVerified: winner?.Build?.Succeeded == true,
                LiveVerified: winner?.Live?.Succeeded == true,
                CandidateHostKind: candidateHostKind,
                VerificationSummary: winner?.Live?.Summary ?? winner?.Build?.Summary)
            : null;

        return new EngineerRunSummary(
            Ok: ok,
            FlowId: flowId,
            RunId: runId,
            RepoSlug: repoSlug,
            Goal: goal,
            Backend: backend,
            ParallelInstances: parallelInstances,
            Status: status,
            CurrentStage: currentStage,
            CompletedStages: completedStages,
            ConfirmationPlanId: confirmationPlanId,
            TraceId: traceId,
            WinningCommitSha: winningSha,
            PushedRef: run.Publication?.PushedRef,
            PullRequestUrl: run.Publication?.PullRequestUrl,
            EngineerAppUri: engineerUri,
            TasksBoardUri: tasksUri,
            Plan: run.Plan,
            Instances: instanceSummaries,
            DiagnosticsAndTraces: traces,
            SummaryText: summaryText,
            Card: card,
            SelectedCandidateIndex: run.SelectedCandidateIndex,
            Candidates: candidates,
            Harness: run.Harness,
            AwaitingPlanRatification: awaitingRatification,
            RatifiedPlan: run.RatifiedPlan,
            CleanupTargets: effectiveCleanup,
            Slices: effectiveSlices,
            Collaborators: collaborators,
            Verification: run.Verification,
            Receipt: receipt);
    }

    private static string[] CollectDiagnosticsAndTraces(
        EngineerRun run,
        EngineerInstance? winner,
        EngineerCandidateResult[] candidates,
        EngineerTaskSlice[] slices,
        string[] cleanupTargets,
        FlowView? view)
    {
        var lines = new List<string>();
        if (run.Plan is { } plan)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"plan.multi-agent · {plan.Tasks.Length} task(s), {slices.Length} slice(s), {cleanupTargets.Length} cleanup target(s), harness={run.Harness}"));
        }

        var digest = run.RatifiedPlan?.CodeGraphDigest ?? run.Plan?.CodeGraphDigest;
        if (!string.IsNullOrWhiteSpace(digest))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"plan.codegraph · hydrated {Encoding.UTF8.GetByteCount(digest)} B repository symbol/brief digest"));
        }

        var totalSelfRepairs = slices.Sum(static s => s.SelfRepairAttempts);
        if (totalSelfRepairs > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"ino.self-repair · {totalSelfRepairs} bounded intra-slice Roslyn repair turn(s) across {slices.Count(static s => s.SelfRepairAttempts > 0)} slice(s)"));
        }

        if (candidates.Length > 1)
        {
            foreach (var c in candidates.OrderBy(static c => c.Rank))
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"tournament.rank#{c.Rank} · w{c.InstanceIndex + 1} ({c.Strategy}) · {c.Score.Summary}{(c.IsSelected ? " [SELECTED]" : string.Empty)}"));
            }
        }

        if (winner?.RoslynDiagnostics is { } roslynDiags)
        {
            var errCount = roslynDiags.Count(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"roslyn.worktree · {roslynDiags.Length} diagnostic(s), {errCount} error(s)"));
        }

        if (winner?.Build is { } build && !string.IsNullOrWhiteSpace(build.Summary))
        {
            lines.Add(build.Summary);
        }

        if (winner?.Live is { } live)
        {
            if (!string.IsNullOrWhiteSpace(live.Summary))
            {
                lines.Add(live.Summary);
            }

            foreach (var traceLine in live.TraceSummaries)
            {
                if (!string.IsNullOrWhiteSpace(traceLine))
                {
                    lines.Add(traceLine);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(view?.Detail))
        {
            lines.Add(view.Detail);
        }

        return [.. lines];
    }

    private static (bool Ok, string Text, InoCard Card) FormatSummaryAndCard(
        BrainId brain,
        string flowId,
        FlowHead head,
        EngineerRun run,
        EngineerInstance? winner,
        FlowStatus status,
        string currentStage,
        string[] completedStages,
        string repoSlug,
        string backend,
        int parallelInstances,
        string? winningSha,
        string? confirmationPlanId,
        bool awaitingRatification,
        string engineerUri,
        string? viewDetail)
    {
        var shortSha = winningSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        var taskCount = run.Plan?.Tasks.Length ?? 0;

        if (awaitingRatification)
        {
            var sliceCount = run.RatifiedPlan?.Slices.Length ?? taskCount;
            var cleanupCount = run.RatifiedPlan?.CleanupTargets.Length ?? 0;
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat-Engineer run `{flowId}` drafted a mission plan ({sliceCount} disjoint slice(s), {cleanupCount} cleanup target(s)) and is waiting for plan ratification (`RatifyEngineerPlan`) before executing `{run.Harness}` worktrees. Workbench: `{engineerUri}`.");
            var ratifyCard = new InoCard(
                Id: flowId,
                Kind: "engineer",
                Title: $"Ratify Mission Plan: {head.Title}",
                Summary: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sliceCount} parallel slice(s) · {cleanupCount} cleanup target(s) · {parallelInstances}x {backend}. Ratify in Studio ({engineerUri})."),
                SourceNeuronUri: engineerUri,
                Status: "pending",
                SurfaceScopeUri: engineerUri);
            return (true, text, ratifyCard);
        }

        if (status == FlowStatus.Waiting && !string.IsNullOrWhiteSpace(confirmationPlanId))
        {
            var branch = winner?.Lease?.BranchName ?? "ino/engineer";
            var filesCount = winner?.Commit?.ChangedFiles.Length ?? 0;
            var sb = new StringBuilder();
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat-Engineer run `{flowId}` completed {taskCount} Architect+Critic task(s) across {parallelInstances} parallel `{backend}` worktree(s) (`{run.Harness}`) and is waiting for your approval at `approval-gate`.\n"));
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"- Repository: `{repoSlug}` · Winning branch: `{branch}` · Verified commit: `{winningSha}` ({filesCount} file(s))\n"));
            sb.Append($"- Approval plan: `{confirmationPlanId}` (only a human Person can approve pushing and opening the Draft PR).\n");
            sb.Append($"- Workbench deep-link: `{engineerUri}`");

            var confirmationsKey = new NeuronId(brain, "runtime.confirmations", "confirmations");
            var approvalCard = new InoCard(
                Id: confirmationPlanId,
                Kind: "approval",
                Title: $"Engineer ready for approval: {head.Title} ({shortSha})",
                Summary: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{parallelInstances}x {backend} worktrees verified · {filesCount} file(s) on {branch}. Inspect in Engineer ({engineerUri}) or approve below."),
                SourceNeuronUri: $"neuron://{confirmationsKey}",
                Status: "pending",
                SurfaceScopeUri: engineerUri);
            return (true, sb.ToString(), approvalCard);
        }

        if (status == FlowStatus.Completed)
        {
            var prUrl = run.Publication?.PullRequestUrl;
            var pushedRef = run.Publication?.PushedRef;
            var publishLabel = !string.IsNullOrWhiteSpace(prUrl)
                ? $"Draft PR: {prUrl}"
                : $"Pushed ref: {pushedRef}";
            var completedCard = new InoCard(
                Id: flowId,
                Kind: "engineer",
                Title: $"Published: {head.Title}",
                Summary: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{publishLabel} · Cleaned {parallelInstances} {backend} worktree(s)."),
                SourceNeuronUri: engineerUri,
                Status: "completed",
                SurfaceScopeUri: prUrl ?? engineerUri,
                Columns: [repoSlug, string.Create(CultureInfo.InvariantCulture, $"{parallelInstances}x {backend}"), shortSha, publishLabel]);
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat-Engineer run `{flowId}` completed. Verified commit `{winningSha}` published ({publishLabel}) and all {parallelInstances} `{backend}` worktree(s) were cleaned up. Workbench: `{engineerUri}`.");
            return (true, text, completedCard);
        }

        if (status is FlowStatus.Failed or FlowStatus.Cancelled)
        {
            var detail = !string.IsNullOrWhiteSpace(viewDetail)
                ? viewDetail
                : winner?.FailureDetail ?? status.ToString();
            var failedCard = new InoCard(
                Id: flowId,
                Kind: "engineer",
                Title: $"Engineer {status.ToString().ToLowerInvariant()}: {head.Title}",
                Summary: detail,
                SourceNeuronUri: engineerUri,
                Status: "failed",
                SurfaceScopeUri: engineerUri,
                Columns: [repoSlug, string.Create(CultureInfo.InvariantCulture, $"{parallelInstances}x {backend}"), currentStage, detail]);
            var text = $"IntoChat-Engineer run `{flowId}` {status.ToString().ToLowerInvariant()} at `{currentStage}`: {detail}";
            return (false, text, failedCard);
        }

        var runningCard = new InoCard(
            Id: flowId,
            Kind: "engineer",
            Title: $"Engineering: {head.Title}",
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"Stage: {currentStage} ({completedStages.Length}/{head.Steps.Length}) · {parallelInstances}x {backend} worktree(s)."),
            SourceNeuronUri: engineerUri,
            Status: "running",
            SurfaceScopeUri: engineerUri,
            Columns:
            [
                repoSlug,
                string.Create(CultureInfo.InvariantCulture, $"{parallelInstances}x {backend}"),
                currentStage,
                string.Create(CultureInfo.InvariantCulture, $"{completedStages.Length}/{head.Steps.Length}"),
            ]);
        var runningText = string.Create(
            CultureInfo.InvariantCulture,
            $"IntoChat-Engineer run `{flowId}` is {status.ToString().ToLowerInvariant()} at stage `{currentStage}` ({completedStages.Length}/{head.Steps.Length} stages complete across {parallelInstances} `{backend}` worktree(s)). Workbench: `{engineerUri}`.");
        if (status == FlowStatus.Waiting && !string.IsNullOrWhiteSpace(viewDetail))
        {
            // A run that waits without asking for approval says what it waits for.
            runningText = runningText + "\n" + viewDetail;
            runningCard = runningCard with { Summary = runningCard.Summary + " " + viewDetail };
        }

        return (true, runningText, runningCard);
    }
}
