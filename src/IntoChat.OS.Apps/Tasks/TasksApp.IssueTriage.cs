/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino, move, advance, complete, clear-done, approve-issue-draft, dismiss-issue-draft, escalate-issue, ui.command

Workspace task planning & live brain flow orchestration (`app.tasks`) built on `AggregateScreen<TasksWorkspaceState>`
and live `IntoChat.OS.Modules.UI` neurons (`IKanbanBoard`, `IStepper`, `ITabs`, `ITable`, `ICommandBar`).
*/
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.KanbanBoard.Signals;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Stepper.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Table.Signals;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Tasks;

public sealed partial class TasksApp
{
    private async Task<PendingIssueTriageDraft[]> TryReadGitHubIssueDraftsAsync()
    {
        List<string> connectionKeys = [];
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

        if (ConnectionCatalog is { } connectionCatalog)
        {
            try
            {
                var catalog = await connectionCatalog.Read();
                foreach (var row in catalog.Rows.Where(static r => string.Equals(r.ConnectorId, "github", StringComparison.Ordinal)))
                {
                    if (!string.IsNullOrWhiteSpace(row.ConnectionId))
                    {
                        connectionKeys.Add(string.Concat("connection/github/", Uri.EscapeDataString(row.ConnectionId)));
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "Connection catalog is optional when probing GitHub triage drafts.");
            }
        }

        if (!connectionKeys.Contains("connection/github/main", StringComparer.Ordinal))
        {
            connectionKeys.Add("connection/github/main");
        }

        var now = NowUtc();
        var results = new List<PendingIssueTriageDraft>();
        var seenPreviews = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connKey in connectionKeys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var repo = Context.Get<IRepository>(new NeuronId(brain, "github", connKey));
                var drafts = await repo.ReadPendingIssueDrafts();
                foreach (var draft in drafts)
                {
                    if (draft.ExpiresAt <= now || !seenPreviews.Add(draft.PreviewId))
                    {
                        continue;
                    }

                    var repoLabel = !string.IsNullOrWhiteSpace(draft.Repository) ? draft.Repository : connKey;
                    results.Add(new PendingIssueTriageDraft(
                        connKey,
                        repoLabel,
                        draft.PreviewId,
                        draft.IssueNumber,
                        draft.Body,
                        draft.SuggestedLabels ?? [],
                        draft.ExpiresAt));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "GitHub connection {ConnectionKey} has no pending issue triage drafts or is unhosted.", connKey);
            }
        }

        return [.. results];
    }

    private static string NormalizeGitHubConnectionKey(string? connectionKey)
    {
        if (string.IsNullOrWhiteSpace(connectionKey))
        {
            return "connection/github/main";
        }

        var trimmed = connectionKey.Trim();
        const string marker = "connection/github/";
        var idx = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var slug = trimmed[(idx + marker.Length)..].Trim('/');
            return slug.Length > 0 ? string.Concat(marker, slug) : "connection/github/main";
        }

        var rawSlug = trimmed.Trim('/');
        return rawSlug.Length > 0 ? string.Concat(marker, rawSlug) : "connection/github/main";
    }

    private async Task<string> StartEngineerFlowForIssueAsync(IFlows flows, string repoLabel, string taskTitle, string goal)
    {
        try
        {
            var request = new EngineerDelegationRequest(
                RepoSlug: repoLabel,
                Goal: goal,
                ParallelInstances: 1,
                Backend: "LocalGit",
                RequirePlanRatification: true);
            var summary = await flows.StartEngineerRun(request);
            if (!summary.Ok && !string.Equals(repoLabel, "os", StringComparison.OrdinalIgnoreCase))
            {
                summary = await flows.StartEngineerRun(request with { RepoSlug = "os" });
            }

            if (summary.Ok && !string.IsNullOrWhiteSpace(summary.FlowId))
            {
                return summary.FlowId;
            }
        }
        catch (NotSupportedException notSupported)
        {
            _logger.LogDebug(notSupported, "StartEngineerRun is unsupported on this IFlows host; starting flow.engineer directly.");
        }

        var fallbackStart = new EngineerStartRequest(
            RunId: string.Empty,
            Repository: "os",
            Goal: goal,
            InstanceCount: 1,
            RequirePlanRatification: true);
        return await fallbackStart.StartOnAsync(flows, title: taskTitle);
    }
}
