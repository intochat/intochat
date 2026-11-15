using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Primitives;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Durable state for <see cref="FeatureToPrFlow"/>. Contains zero caller-settable approval bypass flags:
/// pushing and PR creation require a verified Person-approved <see cref="ConfirmationResult"/> from <see cref="IConfirmationRequests"/>
/// bound to the exact <see cref="CommitSha"/>.
/// </summary>
public sealed class FeatureToPrInner
{
    public string RunId { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Goal { get; set; } = "";
    public string? RequestedBaseBranch { get; set; }

    // Populated by prepare-worktree
    public WorkTreeLease? Lease { get; set; }

    // Populated by edit
    public string? CoderSummary { get; set; }
    public string? CoderModel { get; set; }
    public string[]? CoderTouchedFiles { get; set; }

    // Populated by verify-and-commit
    public string? CommitSha { get; set; }
    public string[] ChangedFiles { get; set; } = [];
    public string? HostRenderedDiff { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }

    // Populated by approval-gate
    public string? ConfirmationPlanId { get; set; }
    public DateTimeOffset? ApprovalExpires { get; set; }

    // Populated by publish (after Person approval)
    public string? ApprovedBy { get; set; }
    public string? PushedRef { get; set; }
    public string? PullRequestUrl { get; set; }
}

/// <summary>
/// Compatibility alias constant preserved for read-only shell views referencing <c>ChangeFlow.Alias</c>.
/// </summary>
public static class ChangeFlow
{
    public const string Alias = FeatureToPrFlow.Alias;
}

/// <summary>
/// Hardened Feature-to-PR durable flow (<c>flow.change</c>) replacing the legacy fake <c>ChangeFlow</c>, <c>PrFlow</c>, and <c>VerifyFlow</c>:
/// <list type="number">
/// <item><description><c>prepare-worktree</c>: Creates an external detached worktree via <see cref="IWorkTree.PrepareAsync"/> and records the Triple Git Lock (R10) anchor.</description></item>
/// <item><description><c>edit</c>: Runs <see cref="ICoder.EditAsync"/> inside the confined worktree.</description></item>
/// <item><description><c>verify-and-commit</c>: Enforces Triple Git Lock + deterministic C# path gate, creates 1 commit as <c>Ino &lt;ino@intochat.ai&gt;</c>, and captures <c>CommitSha</c> + unified diff.</description></item>
/// <item><description><c>approval-gate</c>: Raises a SHA-bound <see cref="ConfirmationPlan"/> (<c>Id = $"app:change:{runId}:{sha}"</c>, <c>Subject = sha</c>, <c>RequiresPerson = true</c>, <c>Expiry = 48h</c>) and parks waiting for human Person approval.</description></item>
/// <item><description><c>publish</c>: Performs Doer Defense-in-Depth (<c>IConfirmationRequests.Outcome</c> verified <c>Approved &amp;&amp; !Skipped &amp;&amp; Approver.IsHuman</c> + <c>HEAD == sha</c> + <c>1</c> commit ahead), pushes <c>&lt;sha&gt;:refs/heads/ino/&lt;branch&gt;</c>, opens a Draft PR from an empty temp directory, and cleans up the worktree.</description></item>
/// </list>
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class FeatureToPrFlow(
    IWorkTree? workTree = null,
    ICoder? coder = null,
    IConfirmationRequests? confirmationRequests = null,
    IServiceProvider? services = null) : Flow<FeatureToPrInner>, IFlowStarter
{
    public const string Alias = "flow.change";
    public const string ConfirmationKind = "f2pr.publish";

    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(48);

    public static readonly string[] Names =
    [
        "prepare-worktree",
        "edit",
        "verify-and-commit",
        "approval-gate",
        "publish",
    ];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override IReadOnlyList<string> Steps => Names;

    private IWorkTree WorkTree => workTree
        ?? services?.GetService(typeof(IWorkTree)) as IWorkTree
        ?? new WorkTreeService();

    private ICoder Coder => coder
        ?? services?.GetService(typeof(ICoder)) as ICoder
        ?? new LocalLlmCoder(services);

    private IConfirmationRequests ConfirmationRequests =>
        confirmationRequests
        ?? services?.GetService(typeof(IConfirmationRequests)) as IConfirmationRequests
        ?? Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        var parsed = JsonSerializer.Deserialize<FeatureToPrInner>(innerJson, Json) ?? new FeatureToPrInner();
        // Sanitize any untrusted caller fields so StartFromJson can never pre-populate approval/commit/publish state
        var sanitized = new FeatureToPrInner
        {
            RunId = parsed.RunId,
            Repository = parsed.Repository,
            Goal = parsed.Goal,
            RequestedBaseBranch = parsed.RequestedBaseBranch,
        };
        return Start(title, owner, parentFlowId, sanitized);
    }

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead(
        [.. Names],
        State.Owner,
        State.TraceId,
        State.ParentFlowId,
        State.Started,
        JsonSerializer.Serialize(State.Inner, Json),
        State.Title,
        State.Status,
        [.. State.Done],
        State.Ended,
        null));

    protected override async Task<StepResult<FeatureToPrInner>> Run(string step, FeatureToPrInner inner, CancellationToken ct)
    {
        try
        {
            return step switch
            {
                "prepare-worktree" => await PrepareWorktreeStepAsync(inner, ct).ConfigureAwait(true),
                "edit" => await EditStepAsync(inner, ct).ConfigureAwait(true),
                "verify-and-commit" => await VerifyAndCommitStepAsync(inner, ct).ConfigureAwait(true),
                "approval-gate" => ApprovalGateStep(step, inner),
                "publish" => await PublishStepAsync(inner, ct).ConfigureAwait(true),
                _ => throw new FlowFailed(step, $"Unknown FeatureToPrFlow step '{step}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Context.Interrupts(ex))
        {
            // Clean up isolated worktree on terminal step failure so no orphan worktree remains on disk
            if (inner.Lease is { } lease)
            {
                try { await WorkTree.CleanupAsync(lease, CancellationToken.None).ConfigureAwait(true); }
                catch (Exception cleanupEx) { NullLogger.Instance.LogSwallowed(cleanupEx, "Best-effort worktree cleanup after failed step."); }
            }

            if (ex is FlowFailed) { throw; }
            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, FeatureToPrInner inner)
    {
        var runId = EffectiveRunId(inner);
        var sha = inner.CommitSha ?? throw new InvalidOperationException("Cannot raise Feature-to-PR confirmation plan without a verified CommitSha.");
        var planId = BuildPlanId(runId, sha);
        var repoLabel = inner.Lease?.GitHubRepo ?? inner.Repository;

        // The body is drawn, never cut and never refused here: the steps refuse what the card cannot show.
        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: $"Push & open Draft PR on {repoLabel} ({sha[..Math.Min(8, sha.Length)]})?",
            Body: ChangeCard.Body(inner),
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: "Discard Worktree",
            TargetLabel: $"{repoLabel}@{sha[..Math.Min(8, sha.Length)]}")
        {
            RequiresPerson = true,
            Expiry = ApprovalWindow,
            Kind = ConfirmationKind,
            Subject = sha,
        };
    }

    public static string BuildPlanId(string runId, string commitSha)
    {
        var cleanRun = string.IsNullOrWhiteSpace(runId) ? "run" : runId.Trim();
        if (cleanRun.Length > 48) { cleanRun = cleanRun[..48]; }
        return $"app:change:{cleanRun}:{commitSha.Trim()}";
    }

    private async Task<StepResult<FeatureToPrInner>> PrepareWorktreeStepAsync(FeatureToPrInner inner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inner.Repository))
        {
            throw new FlowFailed("prepare-worktree", "Repository id is required.");
        }
        if (string.IsNullOrWhiteSpace(inner.Goal))
        {
            throw new FlowFailed("prepare-worktree", "Goal description is required.");
        }

        var runId = EffectiveRunId(inner);
        inner.RunId = runId;

        if (inner.Lease is not null && Directory.Exists(inner.Lease.TreePath))
        {
            await WorkTreeService.VerifyTripleGitLockAsync(inner.Lease, ct).ConfigureAwait(true);
            return inner.Advance(new FlowSubject(Id.ToString(), $"{inner.Repository}:{inner.Lease.BranchName}"));
        }

        var wt = WorkTree;
        var lease = await Task.Run(
            () => wt.PrepareAsync(inner.Repository, runId, inner.Goal, inner.RequestedBaseBranch, ct),
            ct).ConfigureAwait(true);
        inner.Lease = lease;
        return inner.Advance(new FlowSubject(Id.ToString(), $"{lease.RepositoryId}:{lease.BranchName}"));
    }

    private async Task<StepResult<FeatureToPrInner>> EditStepAsync(FeatureToPrInner inner, CancellationToken ct)
    {
        var lease = inner.Lease ?? throw new FlowFailed("edit", "Worktree lease is missing.");
        await WorkTreeService.VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(true);

        var activeCoder = Coder;
        var editResult = await Task.Run(() => activeCoder.EditAsync(lease, inner.Goal, ct), ct).ConfigureAwait(true);
        inner.CoderSummary = editResult.Summary;
        inner.CoderModel = editResult.ModelId;
        inner.CoderTouchedFiles = [.. editResult.TouchedFiles];
        return inner.Advance();
    }

    private async Task<StepResult<FeatureToPrInner>> VerifyAndCommitStepAsync(FeatureToPrInner inner, CancellationToken ct)
    {
        var lease = inner.Lease ?? throw new FlowFailed("verify-and-commit", "Worktree lease is missing.");
        var commitTitle = BuildCommitMessage(inner.Goal, inner.CoderSummary);
        var wt = WorkTree;
        var committed = await Task.Run(() => wt.VerifyAndCommitAsync(lease, commitTitle, ct), ct).ConfigureAwait(true);

        inner.CommitSha = committed.CommitSha;
        inner.ChangedFiles = [.. committed.ChangedFiles];
        inner.Additions = committed.Additions;
        inner.Deletions = committed.Deletions;

        // Asked before the diff is stored: a failed run saves this same state, and a diff no card can show stays out of it.
        if (ChangeCard.Refusal(inner, committed.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed("verify-and-commit", refusal);
        }

        inner.HostRenderedDiff = committed.HostRenderedDiff;
        return inner.Advance();
    }

    private StepResult<FeatureToPrInner> ApprovalGateStep(string step, FeatureToPrInner inner)
    {
        var sha = inner.CommitSha ?? throw new FlowFailed(step, "Cannot enter approval-gate without a verified CommitSha.");

        // Asked again here for a run that committed before the limit existed: no plan is raised for a change the card cannot show.
        if (ChangeCard.Refusal(inner, inner.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed(step, refusal);
        }

        var runId = EffectiveRunId(inner);
        var planId = BuildPlanId(runId, sha);
        var expires = Context.Time.GetUtcNow().Add(ApprovalWindow);

        inner.ConfirmationPlanId = planId;
        inner.ApprovalExpires = expires;

        return inner.WaitApproval(step, expires);
    }

    private async Task<StepResult<FeatureToPrInner>> PublishStepAsync(FeatureToPrInner inner, CancellationToken ct)
    {
        var lease = inner.Lease ?? throw new FlowFailed("publish", "Worktree lease is missing.");
        var sha = inner.CommitSha ?? throw new FlowFailed("publish", "Verified CommitSha is missing.");
        var planId = inner.ConfirmationPlanId ?? BuildPlanId(EffectiveRunId(inner), sha);

        // Idempotent guard if already published
        if (!string.IsNullOrWhiteSpace(inner.PushedRef))
        {
            return inner.Advance();
        }

        // DOER DEFENSE-IN-DEPTH (R7): Re-query ConfirmationsNeuron / IConfirmationRequests directly!
        // Refuse if skipped by policy, denied/expired, or approved by any non-Person principal!
        var outcome = await ConfirmationRequests.Outcome(planId).ConfigureAwait(true);
        if (outcome is null)
        {
            throw new SecurityException($"Refusing to publish: no authoritative confirmation outcome exists for '{planId}'.");
        }
        if (outcome.Outcome != ConfirmationOutcome.Approved || outcome.Skipped)
        {
            throw new SecurityException(
                $"Refusing to publish: confirmation '{planId}' is not explicitly human-approved (Outcome={outcome.Outcome}, Skipped={outcome.Skipped}).");
        }
        if (outcome.Approver is not { IsHuman: true } humanApprover)
        {
            throw new SecurityException(
                $"Refusing to publish: confirmation '{planId}' approver '{outcome.Approver?.Id ?? "none"}' ({outcome.Approver?.Kind}) is not a human Person.");
        }

        var prTitle = BuildPrTitle(inner.Goal);
        var prBody = BuildPrBody(inner, sha, humanApprover.Id);
        var wt = WorkTree;
        var published = await Task.Run(
            () => wt.PublishAsync(lease, sha, prTitle, prBody, ct),
            ct).ConfigureAwait(true);

        inner.ApprovedBy = humanApprover.Id;
        inner.PushedRef = published.PushedRef;
        inner.PullRequestUrl = published.PullRequestUrl;
        return inner.Advance();
    }

    private string EffectiveRunId(FeatureToPrInner inner)
    {
        if (!string.IsNullOrWhiteSpace(inner.RunId))
        {
            return inner.RunId.Trim();
        }

        try { return Id.Key; }
        catch (InvalidOperationException) { return Guid.NewGuid().ToString("N")[..12]; }
    }

    private static string BuildCommitMessage(string goal, string? summary)
    {
        var firstLine = goal.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Automated change";
        if (firstLine.Length > 72) { firstLine = firstLine[..72].TrimEnd() + "..."; }
        var title = firstLine.Contains(':', StringComparison.Ordinal) ? firstLine : $"feat(ino): {firstLine}";
        return string.IsNullOrWhiteSpace(summary) ? title : $"{title}\n\n{summary.Trim()}";
    }

    private static string BuildPrTitle(string goal)
    {
        var firstLine = goal.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Automated change";
        return firstLine.Length <= 80 ? firstLine : firstLine[..80].TrimEnd() + "...";
    }

    private static string BuildPrBody(FeatureToPrInner inner, string sha, string approverId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(string.IsNullOrWhiteSpace(inner.CoderSummary) ? inner.Goal : inner.CoderSummary);
        sb.AppendLine();
        sb.AppendLine("## Verification & Provenance");
        sb.AppendLine($"- **Goal:** {inner.Goal}");
        sb.AppendLine($"- **Commit:** `{sha}`");
        sb.AppendLine($"- **Files ({inner.ChangedFiles.Length}):** {string.Join(", ", inner.ChangedFiles.Select(f => $"`{f}`"))}");
        sb.AppendLine($"- **Approved by Person:** `{approverId}` (plan `{inner.ConfirmationPlanId}`)");
        if (!string.IsNullOrWhiteSpace(inner.CoderModel))
        {
            sb.AppendLine($"- **Model:** `{inner.CoderModel}`");
        }
        return sb.ToString().Trim();
    }
}
