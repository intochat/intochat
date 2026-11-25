/*
# IntoChat Engineer
Role: Studio
Shipped: true
Operations: open, new-run, brainstorm, approve-plan, run-verify, approve-pr, clean-worktree, select-tab, select-instance, select-candidate, reply-review, ui.command

Autonomous multi-worktree engineering workbench (`app.engineer`) orchestrating `flow.engineer` across 1..N isolated
local git worktrees or cloud sandbox volumes with User + Architect + Critic brainstorming, Roslyn C# + Dart code edits,
Release build/test + live Playwright/MCP verification, parallel candidate tournament scoring, and SHA-bound human PR approval.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Engineer;

public sealed partial class EngineerApp
{
    private async Task<EngineerWorkspaceState> SyncLiveRepositoryReviewAsync(
        EngineerWorkspaceState basis,
        int? preferredPrNumber = null,
        PullRequestChanged? trigger = null)
    {
        var (repo, view) = await TryResolveConnectedRepositoryAsync(basis.RepositoryId);
        if (repo is null || view is null)
        {
            return basis;
        }

        try
        {
            var requiredChecksRead = await repo.ReadRequiredChecks();
            var requiredNames = requiredChecksRead.Checks
                .Select(static r => r.Name)
                .Where(static n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var parsedFromUrl = TryParsePullRequestNumber(basis.PullRequestUrl);
            var prNumber = preferredPrNumber
                ?? basis.LivePullRequestNumber
                ?? (parsedFromUrl is { } urlNum && view.PullRequests.Any(p => p.Number == urlNum) ? (int?)urlNum : null)
                ?? view.PullRequests.FirstOrDefault(static p => p.IsOpen)?.Number
                ?? view.PullRequests.FirstOrDefault()?.Number;

            if (prNumber is not > 0)
            {
                var baselineChecks = requiredNames
                    .Select(static name => new EngineerPrCheckItem(name, "pending", null, IsRequired: true))
                    .ToArray();
                return basis.ApplyLivePullRequestReview(
                    prNumber: null,
                    prUrl: basis.PullRequestUrl,
                    checks: baselineChecks,
                    threads: basis.EffectiveLivePrReviewThreads,
                    summary: $"Connected ({view.ConnectionId}) · {requiredNames.Count} required check(s)");
            }

            var prRead = await repo.ReadPullRequest(new ReadPullRequest(prNumber.Value));
            var snapshot = prRead.Snapshot;
            var evidenceRead = await repo.ReadReviewEvidence(new ReadReviewEvidence(snapshot));

            var checkItems = snapshot.Checks
                .Select(c => new EngineerPrCheckItem(
                    Name: c.Name,
                    Status: c.State,
                    Conclusion: c.Conclusion,
                    IsRequired: requiredNames.Contains(c.Name)))
                .ToList();
            foreach (var reqName in requiredNames)
            {
                if (!checkItems.Any(c => string.Equals(c.Name, reqName, StringComparison.OrdinalIgnoreCase)))
                {
                    checkItems.Add(new EngineerPrCheckItem(reqName, "pending", null, IsRequired: true));
                }
            }

            var existingReplied = basis.EffectiveLivePrReviewThreads.ToDictionary(static t => t.CommentId);
            var threads = new List<EngineerPrReviewThread>();
            if (evidenceRead.Evidence?.ReviewComments is { Length: > 0 } liveComments)
            {
                foreach (var comment in liveComments)
                {
                    var prev = existingReplied.GetValueOrDefault(comment.Id);
                    threads.Add(new EngineerPrReviewThread(
                        CommentId: comment.Id,
                        Author: comment.Author,
                        Body: comment.Body,
                        FilePath: comment.Path,
                        Replied: prev?.Replied ?? false,
                        ReplyBody: prev?.ReplyBody));
                }
            }

            if (!string.IsNullOrWhiteSpace(trigger?.CommentBody))
            {
                var syntheticId = Math.Abs(HashCode.Combine(trigger.Number, trigger.CommentAuthor, trigger.CommentBody)) + 1L;
                if (!threads.Any(t => string.Equals(t.Body, trigger.CommentBody, StringComparison.Ordinal)))
                {
                    threads.Add(new EngineerPrReviewThread(
                        CommentId: syntheticId,
                        Author: string.IsNullOrWhiteSpace(trigger.CommentAuthor) ? "reviewer" : trigger.CommentAuthor!,
                        Body: trigger.CommentBody!));
                }
            }

            var passedCount = checkItems.Count(static c => string.Equals(c.Conclusion, "success", StringComparison.OrdinalIgnoreCase));
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"PR #{snapshot.Number} · {snapshot.Title} · {passedCount}/{checkItems.Count} check(s) green");

            return basis.ApplyLivePullRequestReview(
                prNumber: snapshot.Number,
                prUrl: snapshot.Url,
                checks: checkItems.ToArray(),
                threads: threads.ToArray(),
                summary: summary);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Live GitHub PR review reconciliation skipped for repository {RepositoryId}.", basis.RepositoryId);
            return basis;
        }
    }

    private static int? TryParsePullRequestNumber(string? pullRequestUrl)
    {
        if (string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            return null;
        }

        const string marker = "/pull/";
        var idx = pullRequestUrl.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var tail = pullRequestUrl[(idx + marker.Length)..];
        var len = 0;
        while (len < tail.Length && char.IsAsciiDigit(tail[len]))
        {
            len++;
        }

        return len > 0 && int.TryParse(tail[..len], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && num > 0
            ? num
            : null;
    }

    private async Task TryFollowOneAsync<TContract, TSignal>(string key)
        where TContract : class
        where TSignal : Signal
    {
        try
        {
            await Follow<TContract, TSignal>(key);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Optional synapse follow for {Key} skipped in minimal standalone host.", key);
        }
    }

    private async Task<EngineerWorkspaceState> TryStartDurableEngineerFlowAsync(EngineerWorkspaceState basis)
    {
        if (_services?.GetService<IWorkTree>() is not { } wt || wt.ListRepositories().Count == 0)
        {
            return basis;
        }

        if (Flows is not { } flows)
        {
            return basis;
        }

        try
        {
            if (!RepositoryId.TryParse(basis.RepositoryId, out var repoId))
            {
                return basis;
            }

            _ = wt.ResolveRepository(repoId!);
            var payload = new EngineerStartRequest(
                RunId: basis.ActiveRunId,
                Repository: repoId!.Value,
                Goal: basis.Goal,
                InstanceCount: basis.InstanceCount,
                Backend: basis.BackendMode).ToStartJson();

            var flowId = await flows.Start(IEngineerRuns.EngineerFlow, Truncate(basis.Goal, 52), null, payload);
            return basis with { ActiveFlowId = flowId };
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Durable flow.engineer start skipped; interactive workbench state remains active.");
            return basis;
        }
    }

    private async Task<EngineerWorkspaceState> SyncLatestOrActiveFlowAsync(EngineerWorkspaceState basis)
    {
        if (!string.IsNullOrWhiteSpace(basis.ActiveFlowId))
        {
            return await SyncFromLiveFlowAsync(basis, basis.ActiveFlowId!);
        }

        if (Flows is not { } flows)
        {
            return basis;
        }

        try
        {
            var recent = await flows.List(new FlowQuery(Type: IEngineerRuns.EngineerFlow, Limit: 1));
            if (recent.Length > 0)
            {
                return await SyncFromLiveFlowAsync(basis, recent[0].FlowId);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Skipping initial flow.engineer discovery in standalone EngineerApp host.");
        }

        return basis;
    }

    private async Task<EngineerWorkspaceState> SyncFromLiveFlowAsync(EngineerWorkspaceState basis, string flowId)
    {
        if (Flows is not { } flows)
        {
            return basis;
        }

        try
        {
            if (EngineerRuns is { } runs)
            {
                var summary = await runs.InspectEngineerRun(flowId);
                if (!string.IsNullOrWhiteSpace(summary.FlowId) && (summary.Ok || summary.Instances.Length > 0 || summary.Plan is not null))
                {
                    return ApplyLiveSummary(basis, summary, preserveActiveTab: false, preferredFilePath: null);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "InspectEngineerRun unavailable for {FlowId}; falling back to FlowView sync.", flowId);
        }

        try
        {
            var view = await flows.Read(flowId);
            if (view is null)
            {
                return basis;
            }

            var stage = EngineerWorkspaceState.NormalizeStage(view.Current ?? view.WaitingStep ?? view.Done.LastOrDefault());
            return basis with
            {
                ActiveFlowId = flowId,
                CurrentStage = stage,
                ConfirmationPlanId = view.ConfirmationPlanId ?? basis.ConfirmationPlanId,
                PlanApproved = view.Done.Contains("prepare-worktree", StringComparer.Ordinal) || basis.PlanApproved,
                VerificationPassed = view.Done.Contains("live-verify", StringComparer.Ordinal) || basis.VerificationPassed,
                PrPublished = view.Status == FlowStatus.Completed || basis.PrPublished,
                WorktreesCleaned = view.Done.Contains("clean", StringComparer.Ordinal) || basis.WorktreesCleaned,
                StatusBanner = $"Live flow {flowId} · {view.Status} ({stage})",
            };
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Could not sync flow {FlowId} into EngineerApp.", flowId);
            return basis;
        }
    }

}
