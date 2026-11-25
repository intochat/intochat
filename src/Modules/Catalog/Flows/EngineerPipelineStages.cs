using System.Globalization;
using System.Text;
using System.Text.Json;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record EngineerPullRequestContext(
    EngineerTaskPlan? Plan,
    int TotalInstances,
    EngineerInstance Winner,
    EngineerCandidateResult[]? TournamentCandidates = null,
    EngineerVerificationPolicy? Verification = null);

/// <summary>
/// The pipeline stages of <see cref="EngineerFlow"/> (<c>flow.engineer</c>): worktrees, the approval gate, push, pull request,
/// cleanup. The retired <see cref="FeatureToPrFlow"/> (<c>flow.change</c>) finishes its in-flight runs through the same code.
/// </summary>
internal static class EngineerPipelineStages
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static readonly TimeSpan DefaultApprovalWindow = TimeSpan.FromHours(48);

    internal static string ResolveEffectiveRunId(string? explicitRunId, Func<string> grainKeyAccessor)
    {
        if (!string.IsNullOrWhiteSpace(explicitRunId))
        {
            return explicitRunId.Trim();
        }

        try
        {
            return grainKeyAccessor();
        }
        catch (InvalidOperationException)
        {
            return Guid.NewGuid().ToString("N")[..12];
        }
    }

    internal static string BuildPlanId(string prefix, string runId, string commitSha)
    {
        var cleanRun = string.IsNullOrWhiteSpace(runId) ? "run" : runId.Trim();
        if (cleanRun.Length > 40)
        {
            cleanRun = cleanRun[..40];
        }

        return $"{prefix}:{cleanRun}:{commitSha.Trim()}";
    }

    internal static FlowHead BuildHead<TInner>(
        IReadOnlyList<string> stepNames,
        string owner,
        string traceId,
        string? parentFlowId,
        DateTimeOffset started,
        TInner inner,
        string title,
        FlowStatus status,
        IReadOnlyList<string> done,
        DateTimeOffset? ended)
        => new(
            [.. stepNames],
            owner,
            traceId,
            parentFlowId,
            started,
            JsonSerializer.Serialize(inner, Json),
            title,
            status,
            [.. done],
            ended,
            null);

    internal static ConfirmationPlan BuildConfirmationPlan(
        string planId,
        string confirmationKind,
        string denyLabel,
        TimeSpan approvalWindow,
        Change selectedChange,
        CommitSha verifiedCommit)
    {
        var sha = verifiedCommit.Value;
        var shortSha = sha[..Math.Min(8, sha.Length)];
        var repoLabel = selectedChange.Tree?.GitHubRepo
            ?? selectedChange.Tree?.RepositoryId
            ?? selectedChange.Repository?.Value
            ?? string.Empty;

        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: $"Push & open Draft PR on {repoLabel} ({shortSha})?",
            Body: ChangeCard.Body(selectedChange),
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: denyLabel,
            TargetLabel: $"{repoLabel}@{shortSha}")
        {
            RequiresPerson = true,
            Expiry = approvalWindow,
            Kind = confirmationKind,
            Subject = sha,
        };
    }

    internal static async Task<(IReadOnlyList<WorkTreeLease> Leases, FlowSubject Subject)> PrepareWorktreesAsync(
        IWorkTree workTree,
        RepositoryId? repository,
        string goal,
        string runId,
        int instanceCount,
        BranchName? requestedBaseBranch,
        IReadOnlyList<WorkTreeLease> existingLeases,
        string flowId,
        string stepName,
        CancellationToken ct,
        string? requestedBackend = null)
    {
        if (repository is null)
        {
            throw new FlowFailed(stepName, "Repository id is required.");
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new FlowFailed(stepName, "Goal description is required.");
        }

        var desiredBackend = EngineerRun.NormalizeBackend(requestedBackend) is { } normalized
            && Enum.TryParse<WorkTreeBackendKind>(normalized, ignoreCase: true, out var parsedKind)
            ? (WorkTreeBackendKind?)parsedKind
            : null;

        if (existingLeases.Count == instanceCount && existingLeases.All(static l => Directory.Exists(l.TreePath)))
        {
            foreach (var existing in existingLeases)
            {
                await WorkTreeService.VerifyTripleGitLockAsync(existing, ct).ConfigureAwait(true);
            }

            var alignedExisting = desiredBackend is { } keepKind
                ? existingLeases.Select(l => l.Backend == keepKind ? l : l with { Backend = keepKind }).ToArray()
                : existingLeases;
            return (alignedExisting, new FlowSubject(flowId, $"{repository.Value}:{alignedExisting[0].BranchName}"));
        }

        IReadOnlyList<WorkTreeLease> leases = instanceCount == 1
            ? [await Task.Run(() => workTree.PrepareAsync(repository, runId, goal, requestedBaseBranch, ct), ct).ConfigureAwait(true)]
            : await Task.Run(() => workTree.PrepareParallelAsync(repository, runId, goal, instanceCount, requestedBaseBranch, ct), ct).ConfigureAwait(true);

        if (desiredBackend is { } overrideKind)
        {
            leases = leases.Select(l => l.Backend == overrideKind ? l : l with { Backend = overrideKind }).ToArray();
        }

        return (leases, new FlowSubject(flowId, $"{leases[0].RepositoryId}:{leases[0].BranchName}"));
    }

    internal static async Task<ChangeSet> VerifyCommitAndCheckCardAsync(
        IWorkTree workTree,
        WorkTreeLease? lease,
        string goal,
        string? summary,
        Func<ChangeSet, Change> previewChangeFactory,
        string stepName,
        CancellationToken ct)
    {
        var activeLease = lease ?? throw new FlowFailed(stepName, "Worktree lease is missing.");
        var commitTitle = ChangePolicy.BuildCommitMessage(goal, summary);
        var committed = await Task.Run(() => workTree.VerifyAndCommitAsync(activeLease, commitTitle, ct), ct).ConfigureAwait(true);

        EnsureCardAcceptsDiff(previewChangeFactory, committed, stepName);
        return committed;
    }

    internal static void EnsureCardAcceptsDiff(
        Func<ChangeSet, Change> previewChangeFactory,
        ChangeSet committed,
        string stepName)
    {
        var metadataWithoutDiff = ChangeSet.Create(
            committed.Commit,
            committed.ChangedFiles,
            string.Empty,
            committed.Additions,
            committed.Deletions);
        var preview = previewChangeFactory(metadataWithoutDiff);
        if (ChangeCard.Refusal(preview, committed.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed(stepName, refusal);
        }
    }

    internal static (string PlanId, DateTimeOffset Expires) PrepareApprovalGate(
        Change selectedChange,
        ChangeSet? commit,
        string planPrefix,
        string runId,
        DateTimeOffset now,
        TimeSpan approvalWindow,
        string stepName)
    {
        if (commit is null)
        {
            throw new FlowFailed(stepName, "Cannot enter approval-gate without a verified CommitSha.");
        }

        if (ChangeCard.Refusal(selectedChange, commit.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed(stepName, refusal);
        }

        var planId = BuildPlanId(planPrefix, runId, commit.Commit.Value);
        return (planId, now.Add(approvalWindow));
    }

    internal static async Task<(string ApprovedBy, CommitSha Commit, string PushedRef)> ExecutePushAsync(
        IWorkTree workTree,
        IConfirmationRequests confirmationRequests,
        WorkTreeLease? lease,
        ChangeSet? commit,
        string planId,
        Func<ConfirmationResult?, CommitSha, string> ensureCanPublish,
        CancellationToken ct,
        IRepository? repository = null)
    {
        var activeLease = lease ?? throw new FlowFailed("push", "Worktree lease is missing.");
        var verifiedCommit = commit ?? throw new FlowFailed("push", "Verified CommitSha is missing.");

        var outcome = await confirmationRequests.Outcome(planId).ConfigureAwait(true);
        var humanApprover = ensureCanPublish(outcome, verifiedCommit.Commit);

        if (repository is not null)
        {
            await repository.PushBranch(new PushBranchRequest(activeLease.BranchName, verifiedCommit.Commit.Value)).ConfigureAwait(true);
            return (humanApprover, verifiedCommit.Commit, $"refs/heads/{activeLease.BranchName}");
        }

        var pushedRef = await Task.Run(() => workTree.PushAsync(activeLease, verifiedCommit.Commit, ct), ct).ConfigureAwait(true);

        return (humanApprover, verifiedCommit.Commit, pushedRef);
    }

    internal static async Task<string?> ExecutePullRequestAsync(
        IWorkTree workTree,
        WorkTreeLease? lease,
        ChangeSet? commit,
        string? approverId,
        string? planId,
        string goal,
        string? summary,
        string? modelId,
        EngineerPullRequestContext? engineerContext,
        CancellationToken ct,
        IRepository? repository = null)
    {
        var activeLease = lease ?? throw new FlowFailed("pr", "Worktree lease is missing.");
        var verifiedCommit = commit ?? throw new FlowFailed("pr", "Verified CommitSha is missing.");
        var humanApprover = approverId ?? throw new FlowFailed("pr", "Human approver is missing.");

        var prTitle = ChangePolicy.BuildPrTitle(goal);
        var prBody = BuildPullRequestBody(goal, summary, modelId, verifiedCommit, humanApprover, planId, engineerContext);

        if (repository is not null)
        {
            var baseBranch = string.IsNullOrWhiteSpace(activeLease.BaseBranch) ? "main" : activeLease.BaseBranch;
            var created = await repository.CreatePullRequest(new CreatePullRequest(
                Title: prTitle,
                HeadBranch: activeLease.BranchName,
                BaseBranch: baseBranch,
                Body: prBody,
                Draft: true)).ConfigureAwait(true);
            return created.Url;
        }

        return await Task.Run(
            () => workTree.CreateDraftPullRequestAsync(activeLease, prTitle, prBody, ct),
            ct).ConfigureAwait(true);
    }

    internal static async Task ExecuteCleanupAsync(
        IWorkTree workTree,
        IReadOnlyList<WorkTreeLease> activeLeases,
        CancellationToken ct)
    {
        if (activeLeases.Count == 0)
        {
            return;
        }

        if (activeLeases.Count == 1)
        {
            var single = activeLeases[0];
            await Task.Run(() => workTree.CleanupAsync(single, ct), ct).ConfigureAwait(true);
            return;
        }

        await Task.Run(() => workTree.CleanupParallelAsync(activeLeases, ct), ct).ConfigureAwait(true);
    }

    internal static async Task CleanupFailedLeasesBestEffortAsync(
        IWorkTree workTree,
        IReadOnlyList<WorkTreeLease> activeLeases,
        bool alreadyCleaned,
        ILogger logger)
    {
        if (alreadyCleaned || activeLeases.Count == 0)
        {
            return;
        }

        try
        {
            if (activeLeases.Count == 1)
            {
                await workTree.CleanupAsync(activeLeases[0], CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await workTree.CleanupParallelAsync(activeLeases, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException)
        {
            logger.LogSwallowed(cleanupEx, "Best-effort worktree cleanup after failed pipeline step.");
        }
    }

    internal static string BuildPullRequestBody(
        string goal,
        string? summary,
        string? modelId,
        ChangeSet verifiedCommit,
        string approverId,
        string? planId,
        EngineerPullRequestContext? engineerContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        var cleanSummary = ChangePolicy.SanitizeCommitText(summary);
        sb.AppendLine(string.IsNullOrWhiteSpace(cleanSummary) ? ChangePolicy.SanitizeCommitText(goal) : cleanSummary);

        if (engineerContext?.Plan is { } plan)
        {
            sb.AppendLine();
            sb.AppendLine("## Architecture & Plan");
            sb.AppendLine($"- **Architect:** {ChangePolicy.SanitizeCommitText(plan.ArchitectureSummary)}");
            sb.AppendLine($"- **Critic:** {ChangePolicy.SanitizeCommitText(plan.CriticReview)}");
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- **Tasks Completed ({engineerContext.Winner.CompletedTaskIds.Length}/{plan.Tasks.Length}):** {string.Join(", ", engineerContext.Winner.CompletedTaskIds)}"));
        }

        sb.AppendLine();
        sb.AppendLine("## Verification & Provenance");
        sb.AppendLine($"- **Goal:** {ChangePolicy.SanitizeCommitText(goal)}");
        sb.AppendLine($"- **Commit:** `{verifiedCommit.Commit.Value}`");

        if (engineerContext is { } ctx)
        {
            var winnerScore = ctx.Winner.EffectiveScore;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- **Parallel Candidates:** `{ctx.TotalInstances}` (selected candidate `w{ctx.Winner.InstanceIndex + 1}` / `#{ctx.Winner.InstanceIndex}`, strategy `{ctx.Winner.EffectiveStrategy}`, score `{winnerScore.TotalScore}`, lease `{ctx.Winner.Lease?.EffectiveLeaseId}`, backend `{ctx.Winner.Lease?.Backend}`)"));

            if (ctx.TournamentCandidates is { Length: > 1 } ranked)
            {
                sb.AppendLine("- **Candidate Tournament Scoreboard:**");
                foreach (var c in ranked.OrderBy(static c => c.Rank))
                {
                    var shortSha = c.Score.CommitSha is { Length: >= 8 } sha ? sha[..8] : "none";
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  - Rank #{c.Rank}: `w{c.InstanceIndex + 1}` (`{c.Strategy}`) — Score `{c.Score.TotalScore}`, Δ`{c.Score.ChangedLines}` lines, `{shortSha}`{(c.IsSelected ? " **[SELECTED]**" : string.Empty)}"));
                }
            }
        }

        var files = verifiedCommit.ChangedFiles;
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- **Files ({files.Length}):** {string.Join(", ", files.Select(static f => $"`{f.Value}`"))}"));

        if (engineerContext?.Winner.Build is { } build)
        {
            sb.AppendLine($"- **Build & Test:** {ChangePolicy.SanitizeCommitText(build.Summary)}");
        }

        if (engineerContext?.Winner.Live is { } live)
        {
            sb.AppendLine($"- **Live Verify:** {ChangePolicy.SanitizeCommitText(live.Summary)}");
        }

        if (engineerContext?.Verification is { SkipsAny: true } skippedPolicy)
        {
            sb.AppendLine($"- **Verification skipped:** {string.Join(", ", skippedPolicy.SkippedGates.Select(static gate => $"`{gate}`"))} (switched off by `{skippedPolicy.SkippedBy ?? "the starter"}` when the run was started)");
        }

        if (engineerContext?.Verification is { LiveVerifyNotAvailable: true })
        {
            sb.AppendLine("- **Not live-verified:** `live-verify` did not run because no live environment is available on the installation that made this change; it was not checked against a running system.");
        }

        sb.AppendLine($"- **Approved by Person:** `{approverId}` (plan `{planId}`)");
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            sb.AppendLine($"- **Model:** `{modelId}`");
        }

        return sb.ToString().Trim();
    }
}
