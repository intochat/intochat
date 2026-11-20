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
        string? flowId = null)
    {
        var resolvedFlowId = flowId ?? string.Empty;
        var appKey = IntoChat.OS.Primitives.AppRef.Of(brain, "engineer").Key;
        var tasksKey = IntoChat.OS.Primitives.AppRef.Of(brain, "tasks").Key;
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
            SummaryText: errorMessage);
    }

    public static EngineerRunSummary Build(
        BrainId brain,
        string flowId,
        FlowView? view,
        FlowHead head)
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

        var winner = run.SelectedInstance;
        var winningSha = winner?.Commit?.Commit.Value;
        var confirmationPlanId = view?.ConfirmationPlanId ?? run.Approval?.PlanId;
        var traceId = !string.IsNullOrWhiteSpace(view?.TraceId) ? view!.TraceId : head.TraceId;

        var engineerUri = $"neuron://{IntoChat.OS.Primitives.AppRef.Of(brain, "engineer").Key}#{flowId}";
        var tasksUri = $"neuron://{IntoChat.OS.Primitives.AppRef.Of(brain, "tasks").Key}#{flowId}";

        var instanceSummaries = run.Instances
            .Select(static inst =>
            {
                var lease = inst.Lease;
                var changed = inst.Commit?.ChangedFiles.Select(static f => f.Value).ToArray()
                    ?? inst.Edit?.TouchedFiles.Select(static f => f.Value).ToArray()
                    ?? [];
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
                    FailureDetail: inst.FailureDetail);
            })
            .ToArray();

        var traces = CollectDiagnosticsAndTraces(run, winner, view);
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
            engineerUri,
            view?.Detail);

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
            Card: card);
    }

    private static string[] CollectDiagnosticsAndTraces(
        EngineerRun run,
        EngineerInstance? winner,
        FlowView? view)
    {
        var lines = new List<string>();
        if (run.Plan is { } plan)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"plan.multi-agent · {plan.Tasks.Length} task(s), {plan.Transcript.Length} brainstorm turn(s) (Architect + Critic verified)"));
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
        string engineerUri,
        string? viewDetail)
    {
        var shortSha = winningSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        var taskCount = run.Plan?.Tasks.Length ?? 0;

        if (status == FlowStatus.Waiting && !string.IsNullOrWhiteSpace(confirmationPlanId))
        {
            var branch = winner?.Lease?.BranchName ?? "ino/engineer";
            var filesCount = winner?.Commit?.ChangedFiles.Length ?? 0;
            var sb = new StringBuilder();
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat-Engineer run `{flowId}` completed {taskCount} Architect+Critic task(s) across {parallelInstances} parallel `{backend}` worktree(s) and is waiting for your approval at `approval-gate`.\n"));
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
        return (true, runningText, runningCard);
    }
}
