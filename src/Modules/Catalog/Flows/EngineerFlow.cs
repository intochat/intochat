using System.Globalization;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Primitives;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Autonomous multi-stage engineering durable flow (<c>flow.engineer</c>) orchestrating 1..N isolated worktrees
/// across brainstorm/plan, worktree preparation, Roslyn + Dart checkpointed task execution, build/test,
/// live verification, and SHA-bound human approval before branch push, draft PR, and worktree cleanup.
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
    IServiceProvider? services = null) : Flow<EngineerRun>, IFlowStarter
{
    public const string Alias = IFlows.EngineerFlow;
    public const string ConfirmationKind = "engineer.publish";

    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(48);

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

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<EngineerFlow>();

    protected override IReadOnlyList<string> Steps => Names;

    private IWorkTree WorkTree => workTree
        ?? services?.GetService(typeof(IWorkTree)) as IWorkTree
        ?? throw new InvalidOperationException("IWorkTree service is not registered.");

    private ICoder Coder => coder
        ?? services?.GetService(typeof(ICoder)) as ICoder
        ?? throw new InvalidOperationException("ICoder service is not registered.");

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
        ?? IWorktreeRoslyn.CreateDefault();

    private IConfirmationRequests ConfirmationRequests =>
        confirmationRequests
        ?? services?.GetService(typeof(IConfirmationRequests)) as IConfirmationRequests
        ?? Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        var sanitized = EngineerRun.FromUntrustedJson(innerJson, Json);
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
                "approval-gate" => ApprovalGateStep(step, inner),
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
            if (activeLeases.Length > 0 && inner.Publication?.Cleaned != true)
            {
                try
                {
                    await WorkTree.CleanupParallelAsync(activeLeases, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException)
                {
                    OsConsoleLog.For<EngineerFlow>().LogSwallowed(cleanupEx, "Best-effort parallel worktree cleanup after failed step.");
                }
            }

            if (ex is FlowFailed)
            {
                throw;
            }

            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, EngineerRun inner)
    {
        var runId = EffectiveRunId(inner);
        var winner = inner.SelectedInstance
            ?? throw new InvalidOperationException("Cannot raise Engineer confirmation plan without a verified candidate instance.");
        var sha = winner.Commit?.Commit.Value
            ?? throw new InvalidOperationException("Cannot raise Engineer confirmation plan without a verified CommitSha.");
        var planId = BuildPlanId(runId, sha);
        var repoLabel = winner.Lease?.GitHubRepo ?? winner.Lease?.RepositoryId ?? inner.Repository?.Value ?? string.Empty;

        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: $"Push & open Draft PR on {repoLabel} ({sha[..Math.Min(8, sha.Length)]})?",
            Body: ChangeCard.Body(inner.ToSelectedChange()),
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: "Discard Worktrees",
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
        if (cleanRun.Length > 48)
        {
            cleanRun = cleanRun[..48];
        }

        return $"app:engineer:{cleanRun}:{commitSha.Trim()}";
    }

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

        if (inner.Plan is not null && inner.Plan.Validate(ChangePolicy.Default.MaxChangedFiles) is null)
        {
            return inner.Advance();
        }

        var activePlanner = Planner;
        var allowed = ChangePolicy.Default.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var plan = await Task.Run(
            () => activePlanner.PlanAsync(inner.Repository.Value, inner.Goal, allowed, ChangePolicy.Default.MaxChangedFiles, ct),
            ct).ConfigureAwait(true);

        if (plan.Validate(ChangePolicy.Default.MaxChangedFiles) is { } validationError)
        {
            throw new FlowFailed("brainstorm-and-plan", validationError);
        }

        return inner.WithPlan(plan).Advance();
    }

    private async Task<StepResult<EngineerRun>> PrepareWorktreeStepAsync(EngineerRun inner, CancellationToken ct)
    {
        var repo = inner.Repository ?? throw new FlowFailed("prepare-worktree", "Repository id is required.");
        if (inner.Plan is null)
        {
            throw new FlowFailed("prepare-worktree", "EngineerTaskPlan is required before preparing worktrees.");
        }

        var runId = EffectiveRunId(inner);
        var existingLeases = inner.Instances.Select(static i => i.Lease).OfType<WorkTreeLease>().ToArray();
        if (existingLeases.Length == inner.InstanceCount && existingLeases.All(static l => Directory.Exists(l.TreePath)))
        {
            foreach (var existing in existingLeases)
            {
                await WorkTreeService.VerifyTripleGitLockAsync(existing, ct).ConfigureAwait(true);
            }

            return inner.WithLeases(runId, existingLeases)
                .Advance(new FlowSubject(Id.ToString(), $"{repo.Value}:{existingLeases[0].BranchName}"));
        }

        var wt = WorkTree;
        var leases = await Task.Run(
            () => wt.PrepareParallelAsync(repo, runId, inner.Goal, inner.InstanceCount, inner.RequestedBaseBranch, ct),
            ct).ConfigureAwait(true);

        return inner.WithLeases(runId, leases)
            .Advance(new FlowSubject(Id.ToString(), $"{leases[0].RepositoryId}:{leases[0].BranchName}"));
    }

    private async Task<StepResult<EngineerRun>> ImplementTasksStepAsync(EngineerRun inner, CancellationToken ct)
    {
        var plan = inner.Plan ?? throw new FlowFailed("implement-tasks", "EngineerTaskPlan is missing.");
        if (inner.Instances.Length == 0)
        {
            throw new FlowFailed("implement-tasks", "No prepared worktree instances available.");
        }

        var orderedTasks = plan.TopologicalOrder();
        var wt = WorkTree;
        var activeCoder = Coder;
        var activeRoslyn = Roslyn;

        var instanceRuns = inner.Instances.Select(instance => Task.Run(
            () => ImplementSingleInstanceAsync(instance, inner.Goal, plan, orderedTasks, wt, activeCoder, activeRoslyn, ct),
            ct)).ToArray();

        var completedInstances = await Task.WhenAll(instanceRuns).ConfigureAwait(true);
        if (completedInstances.All(static i => i.Commit is null))
        {
            var reason = completedInstances.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed during implement-tasks.";
            throw new FlowFailed("implement-tasks", reason);
        }

        var nextSelected = completedInstances.FirstOrDefault(static i => i.Commit is not null)?.InstanceIndex ?? 0;
        var updated = inner.WithUpdatedInstances(completedInstances, nextSelected);

        // Verify approval card size / binary file refusal before storing full diff on the selected candidate.
        if (updated.SelectedInstance?.Commit is { } selectedCommit)
        {
            var metadataOnly = ChangeSet.Create(
                selectedCommit.Commit,
                selectedCommit.ChangedFiles,
                string.Empty,
                selectedCommit.Additions,
                selectedCommit.Deletions);
            var previewChange = updated.ToSelectedChange(metadataOnly);
            if (ChangeCard.Refusal(previewChange, selectedCommit.HostRenderedDiff) is { } refusal)
            {
                throw new FlowFailed("implement-tasks", refusal);
            }
        }

        return updated.Advance();
    }

    private static async Task<EngineerInstance> ImplementSingleInstanceAsync(
        EngineerInstance instance,
        string goal,
        EngineerTaskPlan plan,
        EngineerTaskItem[] orderedTasks,
        IWorkTree wt,
        ICoder activeCoder,
        IWorktreeRoslyn activeRoslyn,
        CancellationToken ct)
    {
        var lease = instance.Lease ?? throw new InvalidOperationException($"WorkTreeLease missing on instance {instance.InstanceIndex}.");
        await WorkTreeService.VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        if (instance.Commit is not null)
        {
            return instance;
        }

        var completedIds = instance.CompletedTaskIds.ToHashSet(StringComparer.Ordinal);
        var touchedAll = new HashSet<RepoPath>();
        if (instance.Edit is not null)
        {
            foreach (var path in instance.Edit.TouchedFiles)
            {
                touchedAll.Add(path);
            }
        }

        var summaries = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.Edit?.Summary))
        {
            summaries.Add(instance.Edit.Summary);
        }

        var modelId = instance.Edit?.ModelId ?? string.Empty;

        try
        {
            foreach (var task in orderedTasks)
            {
                if (completedIds.Contains(task.Id))
                {
                    continue;
                }

                var symbolHint = string.Empty;
                if (task.Language is "csharp" or "mixed")
                {
                    var symbols = await activeRoslyn.InspectSymbolsAsync(lease.TreePath, null, ct).ConfigureAwait(false);
                    if (symbols.Count > 0)
                    {
                        var topSymbols = string.Join(", ", symbols.Take(12).Select(static s => $"{s.Kind} {s.Name} ({s.FilePath})"));
                        symbolHint = $"\nRoslyn worktree symbols: {topSymbols}";
                    }
                }

                var taskGoal = orderedTasks.Length == 1
                    ? goal
                    : $"{goal}\n\n[Task {task.Id}: {task.Title}]\n{task.Instructions}\nArchitect note: {plan.ArchitectureSummary}{symbolHint}";

                var editResult = await activeCoder.EditAsync(lease, taskGoal, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(editResult.Summary))
                {
                    summaries.Add(editResult.Summary);
                }

                if (!string.IsNullOrWhiteSpace(editResult.ModelId))
                {
                    modelId = editResult.ModelId;
                }

                foreach (var rawPath in editResult.TouchedFiles)
                {
                    if (RepoPath.TryParse(rawPath, out var parsed))
                    {
                        touchedAll.Add(parsed!);
                    }
                }

                completedIds.Add(task.Id);
            }

            var csTouched = touchedAll.Where(static p => p.IsCSharp).Select(static p => p.Value).ToArray();
            var roslynDiagnostics = csTouched.Length > 0
                ? (await activeRoslyn.AnalyzeAsync(lease.TreePath, csTouched, ct).ConfigureAwait(false)).ToArray()
                : [];
            var firstRoslynError = roslynDiagnostics.FirstOrDefault(static d => d.Severity == "Error");
            if (firstRoslynError is not null)
            {
                return instance with
                {
                    CompletedTaskIds = [.. completedIds.Order(StringComparer.Ordinal)],
                    RoslynDiagnostics = roslynDiagnostics,
                    Status = "failed",
                    FailureDetail = $"Roslyn error {firstRoslynError.Id} in {firstRoslynError.FilePath}:{firstRoslynError.Line}: {firstRoslynError.Message}",
                };
            }

            var combinedSummary = string.Join(" ", summaries.Where(static s => !string.IsNullOrWhiteSpace(s)));
            var commitMessage = ChangePolicy.BuildCommitMessage(goal, combinedSummary);
            var committed = await wt.VerifyAndCommitAsync(lease, commitMessage, ct).ConfigureAwait(false);

            return instance with
            {
                CompletedTaskIds = [.. completedIds.Order(StringComparer.Ordinal)],
                Edit = new CoderEdit(
                    combinedSummary,
                    modelId,
                    [.. touchedAll.OrderBy(static p => p.Value, StringComparer.Ordinal)]),
                RoslynDiagnostics = roslynDiagnostics,
                Commit = committed,
                Status = "implemented",
                FailureDetail = null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLogger.LogSwallowed(ex, $"engineer worktree instance #{instance.InstanceIndex} implementation failed");
            return instance with
            {
                CompletedTaskIds = [.. completedIds.Order(StringComparer.Ordinal)],
                Status = "failed",
                FailureDetail = ex.Message,
            };
        }
    }

    private async Task<StepResult<EngineerRun>> BuildAndTestStepAsync(EngineerRun inner, CancellationToken ct)
    {
        var verifier = BuildVerifier;
        var tasks = inner.Instances.Select(async instance =>
        {
            if (instance.Commit is null || instance.Lease is null)
            {
                return instance;
            }

            if (instance.Build is { Succeeded: true })
            {
                return instance;
            }

            var changedPaths = instance.Commit.ChangedFiles.Select(static f => f.Value).ToArray();
            var report = await Task.Run(() => verifier.VerifyAsync(instance.Lease.TreePath, changedPaths, ct), ct).ConfigureAwait(false);
            return instance with
            {
                Build = report,
                Status = report.Succeeded ? "built" : "failed",
                FailureDetail = report.Succeeded ? null : report.Summary,
            };
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);
        var passing = results.Where(static i => i.Build is { Succeeded: true }).ToArray();
        if (passing.Length == 0)
        {
            var detail = results.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed build-and-test verification.";
            throw new FlowFailed("build-and-test", detail);
        }

        return inner.WithUpdatedInstances(results, passing[0].InstanceIndex).Advance();
    }

    private async Task<StepResult<EngineerRun>> LiveVerifyStepAsync(EngineerRun inner, CancellationToken ct)
    {
        var verifier = LiveVerifier;
        var tasks = inner.Instances.Select(async instance =>
        {
            if (instance.Build is not { Succeeded: true } || instance.Commit is null || instance.Lease is null)
            {
                return instance;
            }

            if (instance.Live is { Succeeded: true })
            {
                return instance;
            }

            var changedPaths = instance.Commit.ChangedFiles.Select(static f => f.Value).ToArray();
            var report = await Task.Run(
                () => verifier.VerifyLiveAsync(instance.Lease.TreePath, inner.Goal, changedPaths, ct),
                ct).ConfigureAwait(false);
            return instance with
            {
                Live = report,
                Status = report.Succeeded ? "verified" : "failed",
                FailureDetail = report.Succeeded ? null : report.Summary,
            };
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);
        var verified = results
            .Where(static i => i.Live is { Succeeded: true } && i.Build is { Succeeded: true } && i.Commit is not null)
            .OrderBy(static i => i.Build!.WarningCount)
            .ThenBy(static i => i.InstanceIndex)
            .ToArray();

        if (verified.Length == 0)
        {
            var detail = results.Select(static i => i.FailureDetail).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))
                ?? "All worktree instances failed live behavioral verification.";
            throw new FlowFailed("live-verify", detail);
        }

        return inner.WithUpdatedInstances(results, verified[0].InstanceIndex).Advance();
    }

    private StepResult<EngineerRun> ApprovalGateStep(string step, EngineerRun inner)
    {
        var winner = inner.SelectedInstance ?? throw new FlowFailed(step, "No verified candidate instance available.");
        var commit = winner.Commit ?? throw new FlowFailed(step, "Cannot enter approval-gate without a verified CommitSha.");

        if (ChangeCard.Refusal(inner.ToSelectedChange(), commit.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed(step, refusal);
        }

        var runId = EffectiveRunId(inner);
        var planId = BuildPlanId(runId, commit.Commit.Value);
        var expires = Context.Time.GetUtcNow().Add(ApprovalWindow);

        return inner.WithApprovalPending(planId, expires).WaitApproval(step, expires);
    }

    private async Task<StepResult<EngineerRun>> PushStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inner.Publication?.PushedRef))
        {
            return inner.Advance();
        }

        var winner = inner.SelectedInstance ?? throw new FlowFailed("push", "Selected worktree instance is missing.");
        var lease = winner.Lease ?? throw new FlowFailed("push", "Worktree lease is missing.");
        var commit = winner.Commit ?? throw new FlowFailed("push", "Verified CommitSha is missing.");
        var planId = inner.Approval?.PlanId ?? BuildPlanId(EffectiveRunId(inner), commit.Commit.Value);

        var outcome = await ConfirmationRequests.Outcome(planId).ConfigureAwait(true);
        var humanApprover = inner.EnsureCanPublish(outcome, commit.Commit);

        var wt = WorkTree;
        var pushedRef = await Task.Run(() => wt.PushAsync(lease, commit.Commit, ct), ct).ConfigureAwait(true);

        return inner.WithPushed(humanApprover, commit.Commit, pushedRef).Advance();
    }

    private async Task<StepResult<EngineerRun>> PullRequestStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Publication?.PullRequestCompleted == true)
        {
            return inner.Advance();
        }

        var winner = inner.SelectedInstance ?? throw new FlowFailed("pr", "Selected worktree instance is missing.");
        var lease = winner.Lease ?? throw new FlowFailed("pr", "Worktree lease is missing.");
        var commit = winner.Commit ?? throw new FlowFailed("pr", "Verified CommitSha is missing.");
        var approverId = inner.Approval?.ApprovedBy ?? throw new FlowFailed("pr", "Human approver is missing.");

        var prTitle = ChangePolicy.BuildPrTitle(inner.Goal);
        var prBody = BuildPrBody(inner, winner, commit.Commit.Value, approverId);

        var wt = WorkTree;
        var prUrl = await Task.Run(
            () => wt.CreateDraftPullRequestAsync(lease, prTitle, prBody, ct),
            ct).ConfigureAwait(true);

        return inner.WithPullRequest(prUrl).Advance();
    }

    private async Task<StepResult<EngineerRun>> CleanStepAsync(EngineerRun inner, CancellationToken ct)
    {
        if (inner.Publication?.Cleaned == true)
        {
            return inner.Advance();
        }

        var activeLeases = inner.Instances.Select(static i => i.Lease).OfType<WorkTreeLease>().ToArray();
        if (activeLeases.Length > 0)
        {
            var wt = WorkTree;
            await Task.Run(() => wt.CleanupParallelAsync(activeLeases, ct), ct).ConfigureAwait(true);
        }

        return inner.WithCleaned().Advance();
    }

    private string EffectiveRunId(EngineerRun inner)
    {
        if (!string.IsNullOrWhiteSpace(inner.RunId))
        {
            return inner.RunId.Trim();
        }

        try
        {
            return Id.Key;
        }
        catch (InvalidOperationException)
        {
            return Guid.NewGuid().ToString("N")[..12];
        }
    }

    private static string BuildPrBody(EngineerRun inner, EngineerInstance winner, string sha, string approverId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        var cleanSummary = ChangePolicy.SanitizeCommitText(winner.Edit?.Summary);
        sb.AppendLine(string.IsNullOrWhiteSpace(cleanSummary) ? ChangePolicy.SanitizeCommitText(inner.Goal) : cleanSummary);

        if (inner.Plan is { } plan)
        {
            sb.AppendLine();
            sb.AppendLine("## Architecture & Plan");
            sb.AppendLine($"- **Architect:** {ChangePolicy.SanitizeCommitText(plan.ArchitectureSummary)}");
            sb.AppendLine($"- **Critic:** {ChangePolicy.SanitizeCommitText(plan.CriticReview)}");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Tasks Completed ({winner.CompletedTaskIds.Length}/{plan.Tasks.Length}):** {string.Join(", ", winner.CompletedTaskIds)}"));
        }

        sb.AppendLine();
        sb.AppendLine("## Verification & Provenance");
        sb.AppendLine($"- **Goal:** {ChangePolicy.SanitizeCommitText(inner.Goal)}");
        sb.AppendLine($"- **Commit:** `{sha}`");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Parallel Candidates:** `{inner.Instances.Length}` (selected instance `#{winner.InstanceIndex}`, lease `{winner.Lease?.EffectiveLeaseId}`, backend `{winner.Lease?.Backend}`)"));
        var files = winner.Commit?.ChangedFiles ?? [];
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Files ({files.Length}):** {string.Join(", ", files.Select(static f => $"`{f.Value}`"))}"));
        if (winner.Build is { } build)
        {
            sb.AppendLine($"- **Build & Test:** {ChangePolicy.SanitizeCommitText(build.Summary)}");
        }

        if (winner.Live is { } live)
        {
            sb.AppendLine($"- **Live Verify:** {ChangePolicy.SanitizeCommitText(live.Summary)}");
        }

        sb.AppendLine($"- **Approved by Person:** `{approverId}` (plan `{inner.Approval?.PlanId}`)");
        if (!string.IsNullOrWhiteSpace(winner.Edit?.ModelId))
        {
            sb.AppendLine($"- **Model:** `{winner.Edit.ModelId}`");
        }

        return sb.ToString().Trim();
    }
}
