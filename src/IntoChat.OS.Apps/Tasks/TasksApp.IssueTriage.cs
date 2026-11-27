using DigitalBrain;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tasks;

public sealed partial class TasksApp
{
    private async Task<PendingIssueTriageDraft[]> TryReadGitHubIssueDraftsAsync()
    {
        BrainId brain;
        try
        {
            brain = Id.Brain;
        }
        catch (InvalidOperationException error)
        {
            _logger.LogDebug(error, "TasksApp brain id is unavailable outside neuron activation.");
            return [];
        }

        var connectionKeys = new HashSet<string>(StringComparer.Ordinal);
        if (ConnectionCatalog is { } connectionCatalog)
        {
            try
            {
                var catalog = await connectionCatalog.Read();
                foreach (var row in catalog.Rows)
                {
                    if (string.Equals(row.ConnectorId, "github", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(row.ConnectionId))
                    {
                        connectionKeys.Add(string.Concat("connection/github/", Uri.EscapeDataString(row.ConnectionId)));
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "Connection catalog is optional when probing GitHub issue drafts.");
            }
        }

        if (connectionKeys.Count == 0)
        {
            connectionKeys.Add("connection/github/main");
        }

        var now = NowUtc();
        var results = new List<PendingIssueTriageDraft>();
        var seenPreviews = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connKey in connectionKeys)
        {
            try
            {
                var drafts = await Repositories[new NeuronId(brain, "github", connKey)].ReadPendingIssueDrafts();
                foreach (var draft in drafts)
                {
                    if (draft.ExpiresAt <= now || !seenPreviews.Add(draft.PreviewId))
                    {
                        continue;
                    }

                    results.Add(new PendingIssueTriageDraft(
                        connKey,
                        !string.IsNullOrWhiteSpace(draft.Repository) ? draft.Repository : connKey,
                        draft.PreviewId,
                        draft.IssueNumber,
                        draft.Body,
                        draft.SuggestedLabels ?? [],
                        draft.ExpiresAt));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "GitHub connection {ConnectionKey} has no pending issue drafts or is unhosted.", connKey);
            }
        }

        return [.. results];
    }

    private static string NormalizeGitHubConnectionKey(string? connectionKey)
    {
        const string marker = "connection/github/";
        if (string.IsNullOrWhiteSpace(connectionKey))
        {
            return "connection/github/main";
        }

        var trimmed = connectionKey.Trim();
        var idx = trimmed.IndexOf(marker, StringComparison.Ordinal);
        var slug = (idx >= 0 ? trimmed[(idx + marker.Length)..] : trimmed).Trim('/');
        return slug.Length > 0 ? string.Concat(marker, slug) : "connection/github/main";
    }

    private async Task<string> StartEngineerFlowForIssueAsync(IFlows flows, string repoLabel, string taskTitle, string goal)
    {
        if (EngineerRuns is { } runs)
        {
            var request = new EngineerDelegationRequest(
                RepoSlug: repoLabel,
                Goal: goal,
                ParallelInstances: 1,
                Backend: "LocalGit",
                RequirePlanRatification: true);
            var summary = await runs.StartEngineerRun(request);
            if (!summary.Ok && !string.Equals(repoLabel, "os", StringComparison.OrdinalIgnoreCase))
            {
                summary = await runs.StartEngineerRun(request with { RepoSlug = "os" });
            }

            if (summary.Ok && !string.IsNullOrWhiteSpace(summary.FlowId))
            {
                return summary.FlowId;
            }
        }

        return await new EngineerStartRequest(
            RunId: string.Empty,
            Repository: "os",
            Goal: goal,
            InstanceCount: 1,
            RequirePlanRatification: true).StartOnAsync(flows, title: taskTitle);
    }
}
