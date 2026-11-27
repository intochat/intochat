using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain.Flows;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.UI;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Apps.Tasks;

/// <summary>
/// A single task or projected brain flow on the Tasks Kanban board, table, and lifecycle stepper.
/// </summary>
public sealed record TaskWorkItem(
    string Id,
    string Title,
    string Stage,
    string Owner = "owner",
    string Description = "",
    string Tag = "Task",
    string? FlowId = null)
{
    public TaskWorkItem() : this("task-1", "Task", "backlog") { }
}

/// <summary>
/// Draft fields folded from the quick task form and relation lag input.
/// </summary>
public sealed record TasksDraft(string Title = "", string Description = "", string Lag = "");

/// <summary>
/// One projected dependency bar on the workflow timeline.
/// </summary>
public sealed record TimelineBar(string FlowId, string Title, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// One Kanban column and the ordered items assigned to it.
/// </summary>
public sealed record BoardColumn(string Id, string Title, TaskWorkItem[] Items);

/// <summary>
/// Tracks a GitHub issue escalated from <see cref="TasksApp"/> into an <c>Engineer</c> studio run.
/// </summary>
public sealed record EscalatedIssueLink(
    string PreviewId,
    string Repository,
    int IssueNumber,
    string FlowId,
    string Title)
{
    public EscalatedIssueLink() : this("", "os", 0, "", "") { }
}

/// <summary>
/// Immutable DDD Aggregate state for <see cref="TasksApp"/> (<c>app.tasks</c>).
/// </summary>
public sealed record TasksWorkspaceState(
    string ActiveTab,
    FlowQuery Query,
    string? SelectedTaskId,
    TaskWorkItem[] Tasks)
{
    public const int MaxEscalatedIssues = 16;

    public static readonly string[] StageIds = ["backlog", "in-progress", "review", "done"];

    public static readonly string[] TabIds = ["board", "table", "approvals", "list", "details"];

    private static readonly TimeSpan MaxProjection = TimeSpan.FromDays(30);

    public TasksWorkspaceState() : this(
        ActiveTab: "board",
        Query: new FlowQuery(),
        SelectedTaskId: null,
        Tasks: [])
    {
    }

    public string Status { get; init; } = "Ready · Plan, move, and advance tasks across Backlog, In Progress, Review, and Done.";

    public string RelateKind { get; init; } = "BlockedBy";

    public string? Source { get; init; }

    public TasksDraft Draft { get; init; } = new();

    public int NextTaskSequence { get; init; } = 1;

    public string? FocusedConfirmationId { get; init; }

    public string? FocusedDraftPreviewId { get; init; }

    public EscalatedIssueLink[] EscalatedIssues { get; init; } = [];

    [JsonIgnore]
    public string View => ActiveTab;

    [JsonIgnore]
    public string? Selected => SelectedTaskId;

    [JsonIgnore]
    public TaskWorkItem? SelectedTask => SelectedTaskId is { Length: > 0 } id
        ? Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
        : null;

    [JsonIgnore]
    public string? SelectedFlowId => SelectedTask?.FlowId ?? SelectedTaskId;

    [JsonIgnore]
    public bool HasUnsavedDraft => !string.IsNullOrWhiteSpace(Draft.Title) || !string.IsNullOrWhiteSpace(Draft.Description);

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("ActiveTab is always a valid Tasks studio tab."), JsonIgnore]
    public bool ValidActiveTab => TabIds.Contains(ActiveTab, StringComparer.Ordinal);

    [Invariant("Every task resides in a valid Kanban stage (backlog, in-progress, review, or done) with non-empty Id and Title."), JsonIgnore]
    public bool AllTasksValid => Tasks.All(static t =>
        !string.IsNullOrWhiteSpace(t.Id)
        && !string.IsNullOrWhiteSpace(t.Title)
        && StageIds.Contains(t.Stage, StringComparer.Ordinal));

    [Invariant("Task IDs are unique across the workspace."), JsonIgnore]
    public bool UniqueTaskIds => Tasks.Select(static t => t.Id).Distinct(StringComparer.Ordinal).Count() == Tasks.Length;

    [Invariant("RelateKind is always a valid RelationKind."), JsonIgnore]
    public bool ValidRelateKind => Enum.TryParse<RelationKind>(RelateKind, ignoreCase: false, out _);

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule NonEmptyTitle(string? title) => Decision.Rule(
        "empty_title",
        title,
        static t => string.IsNullOrWhiteSpace(t),
        "Enter a non-empty task title.");

    public static Rule ValidStageColumn(string? stage) => Decision.Rule(
        "invalid_stage",
        stage,
        static s => TryNormalizeStage(s) is null,
        "Stage column must be backlog, in-progress, review, or done.");

    public Rule TaskExists(string? taskId) => Decision.Rule(
        "task_not_found",
        (Tasks, TaskId: taskId),
        static x => string.IsNullOrWhiteSpace(x.TaskId) || !x.Tasks.Any(t => string.Equals(t.Id, x.TaskId.Trim(), StringComparison.Ordinal)),
        "Select an existing task on the board.");

    public Rule HasSelectedTask() => Decision.Rule(
        "no_selected_task",
        SelectedTask,
        static selected => selected is null,
        "Select an existing task first.");

    public Rule CanAdvanceSelectedTask() => Decision.Rule(
        "cannot_advance_task",
        SelectedTask,
        static selected => selected is null || string.Equals(selected.Stage, "done", StringComparison.Ordinal),
        static selected => selected is null ? "Select a task to advance its stage." : "Selected task is already in Done.");

    public Rule CanCompleteSelectedTask() => Decision.Rule(
        "already_completed",
        SelectedTask,
        static selected => selected is null || string.Equals(selected.Stage, "done", StringComparison.Ordinal),
        static selected => selected is null ? "Select a task to mark Done." : "Selected task is already Done.");

    public Rule HasCompletedTasks() => Decision.Rule(
        "no_completed_tasks",
        Tasks,
        static tasks => !tasks.Any(static t => string.Equals(t.Stage, "done", StringComparison.Ordinal)),
        "There are no Done tasks to clear.");

    public Rule CanDepart(bool force = false) => Decision.Rule(
        "unsaved_task_draft",
        (HasUnsavedDraft, force),
        static x => x.HasUnsavedDraft && !x.force,
        "Finish or clear the unsaved task draft before leaving Tasks.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public TasksWorkspaceState SelectTab([Sample("board", "table", "approvals", "list", "details")] string? tab)
    {
        var normalized = NormalizeTab(tab);
        return this with
        {
            ActiveTab = normalized,
            Status = $"Viewing {TabTitle(normalized)}",
        };
    }

    public Decision<TasksWorkspaceState> AddTask(
        [Sample("Verify Kanban push sync", "")] string? title,
        [Sample("Exercise KanbanBoardChanged and StepperChanged")] string? description = null,
        [Sample("backlog", "in-progress")] string? stage = "backlog")
    {
        var effectiveStage = stage ?? "backlog";
        return Decision.Check("add-task", NonEmptyTitle(title), ValidStageColumn(effectiveStage))
            ?? Decision.Accept(CreateTaskCore(title!.Trim(), description?.Trim() ?? string.Empty, TryNormalizeStage(effectiveStage)!));
    }

    public Decision<TasksWorkspaceState> MoveTask(
        [Sample("task-2", "missing-task")] string? taskId,
        [Sample("backlog", "in-progress", "review", "done", "invalid-column")] string? targetStage)
    {
        if (Decision.Check("move-task", TaskExists(taskId), ValidStageColumn(targetStage)) is { } rejected)
        {
            return rejected;
        }

        var id = taskId!.Trim();
        var normalized = TryNormalizeStage(targetStage)!;
        var moved = Tasks.First(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        var updated = Tasks
            .Select(t => string.Equals(t.Id, id, StringComparison.Ordinal) ? t with { Stage = normalized } : t)
            .ToArray();

        return Decision.Accept(this with
        {
            Tasks = updated,
            SelectedTaskId = id,
            Source = null,
            Status = $"Moved '{moved.Title}' to {StageTitle(normalized)}.",
        });
    }

    public Decision<TasksWorkspaceState> AdvanceSelectedStage()
    {
        if (Decision.Check("advance-stage", HasSelectedTask(), CanAdvanceSelectedTask()) is { } rejected)
        {
            return rejected;
        }

        var current = SelectedTask!;
        var nextStage = NextStageOf(current.Stage);
        return MoveTask(current.Id, nextStage);
    }

    public Decision<TasksWorkspaceState> CompleteSelectedTask()
    {
        if (Decision.Check("complete-task", HasSelectedTask(), CanCompleteSelectedTask()) is { } rejected)
        {
            return rejected;
        }

        var current = SelectedTask!;
        return MoveTask(current.Id, "done");
    }

    public Decision<TasksWorkspaceState> ClearDoneTasks()
    {
        if (Decision.Check("clear-done", HasCompletedTasks()) is { } rejected)
        {
            return rejected;
        }

        var remaining = Tasks.Where(static t => !string.Equals(t.Stage, "done", StringComparison.Ordinal)).ToArray();
        var clearedCount = Tasks.Length - remaining.Length;
        var nextSelected = SelectedTaskId is not null && remaining.Any(t => string.Equals(t.Id, SelectedTaskId, StringComparison.Ordinal))
            ? SelectedTaskId
            : remaining.FirstOrDefault()?.Id;

        return Decision.Accept(this with
        {
            Tasks = remaining,
            SelectedTaskId = nextSelected,
            Source = null,
            Status = string.Create(CultureInfo.InvariantCulture, $"Cleared {clearedCount} completed task(s)."),
        });
    }

    // ── Screen & Live-Flow Helpers ───────────────────────────────────────────

    [IgnoreExplore]
    public TasksWorkspaceState SelectTaskOrFlow(string? id)
    {
        var trimmed = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        return this with
        {
            SelectedTaskId = trimmed,
            Source = null,
        };
    }

    [IgnoreExplore]
    public TasksWorkspaceState ApplyFilter(string? owner, FlowStatus? status, string? type)
        => this with
        {
            Query = Query with
            {
                Owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim(),
                Status = status,
                Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            },
        };

    [IgnoreExplore]
    public TasksWorkspaceState SetRelateKind(string? kind)
        => Enum.TryParse<RelationKind>(kind, ignoreCase: true, out var parsed)
            ? this with { RelateKind = parsed.ToString() }
            : this;

    [IgnoreExplore]
    public TasksWorkspaceState UpdateDraftField(string field, string? rawValue)
    {
        var value = rawValue ?? string.Empty;
        return this with
        {
            Draft = field switch
            {
                "title" => Draft with { Title = value },
                "description" => Draft with { Description = value },
                _ => Draft with { Lag = value },
            },
        };
    }

    [IgnoreExplore]
    public TasksWorkspaceState MarkStarted(string flowId, string title, string description = "", string stage = "backlog", string tag = "Plan")
    {
        var normalizedStage = TryNormalizeStage(stage) ?? "backlog";
        var effectiveTag = string.IsNullOrWhiteSpace(tag) ? "Plan" : tag.Trim();
        var item = new TaskWorkItem(
            Id: flowId,
            Title: title,
            Stage: normalizedStage,
            Owner: "owner",
            Description: description,
            Tag: effectiveTag,
            FlowId: flowId);

        var existingIdx = Array.FindIndex(Tasks, t => string.Equals(t.Id, flowId, StringComparison.Ordinal));
        TaskWorkItem[] nextTasks;
        if (existingIdx >= 0)
        {
            nextTasks = [.. Tasks];
            nextTasks[existingIdx] = item;
        }
        else
        {
            nextTasks = [.. Tasks, item];
        }

        return this with
        {
            Tasks = nextTasks,
            SelectedTaskId = flowId,
            Source = null,
            Status = $"Started {title}",
            Draft = Draft with { Title = "", Description = "" },
        };
    }

    [IgnoreExplore]
    public string? FindEscalatedFlowId(string? previewId, string? repository, int issueNumber)
    {
        if (!string.IsNullOrWhiteSpace(previewId)
            && EscalatedIssues.FirstOrDefault(e => string.Equals(e.PreviewId, previewId.Trim(), StringComparison.Ordinal)) is { } byPreview)
        {
            return byPreview.FlowId;
        }

        if (issueNumber > 0
            && EscalatedIssues.FirstOrDefault(e =>
                e.IssueNumber == issueNumber
                && (string.IsNullOrWhiteSpace(repository) || string.Equals(e.Repository, repository.Trim(), StringComparison.OrdinalIgnoreCase))) is { } byIssue)
        {
            return byIssue.FlowId;
        }

        return null;
    }

    [IgnoreExplore]
    public TasksWorkspaceState RecordEscalatedIssue(
        string previewId,
        string repository,
        int issueNumber,
        string flowId,
        string title,
        string description)
    {
        var started = MarkStarted(flowId, title, description, stage: "in-progress", tag: "Engineer");
        var normalizedPreview = string.IsNullOrWhiteSpace(previewId)
            ? string.Create(CultureInfo.InvariantCulture, $"issue-{issueNumber}")
            : previewId.Trim();
        var normalizedRepo = string.IsNullOrWhiteSpace(repository) ? "os" : repository.Trim();
        var retained = EscalatedIssues
            .Where(e => !string.Equals(e.PreviewId, normalizedPreview, StringComparison.Ordinal)
                && !(e.IssueNumber == issueNumber && string.Equals(e.Repository, normalizedRepo, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var link = new EscalatedIssueLink(normalizedPreview, normalizedRepo, issueNumber, flowId, title);
        var nextLinks = retained.Append(link).TakeLast(MaxEscalatedIssues).ToArray();
        var nextFocused = string.Equals(started.FocusedDraftPreviewId, normalizedPreview, StringComparison.Ordinal)
            ? null
            : started.FocusedDraftPreviewId;

        return started with
        {
            EscalatedIssues = nextLinks,
            FocusedDraftPreviewId = nextFocused,
            Status = string.Create(CultureInfo.InvariantCulture, $"Escalated {normalizedRepo} #{issueNumber} to Engineer Studio ({flowId})."),
        };
    }

    [IgnoreExplore]
    public TasksWorkspaceState SyncLiveFlows(FlowView[] flows)
    {
        if (flows.Length == 0)
        {
            return this;
        }

        var list = Tasks.ToList();
        foreach (var flow in flows)
        {
            var mappedStage = StageForFlowStatus(flow.Status);
            var tag = ShortFlowType(flow.Type);
            var existingIdx = list.FindIndex(t => string.Equals(t.Id, flow.FlowId, StringComparison.Ordinal) || string.Equals(t.FlowId, flow.FlowId, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                var existing = list[existingIdx];
                // Terminal flow statuses always move to done; active flows keep user Kanban stage if already in-progress/review.
                var nextStage = flow.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled
                    ? "done"
                    : existing.Stage;
                list[existingIdx] = existing with
                {
                    Title = flow.Title,
                    Owner = flow.Owner,
                    Tag = tag,
                    Stage = nextStage,
                    FlowId = flow.FlowId,
                };
            }
            else
            {
                list.Add(new TaskWorkItem(
                    Id: flow.FlowId,
                    Title: flow.Title,
                    Stage: mappedStage,
                    Owner: flow.Owner,
                    Description: flow.Detail ?? string.Empty,
                    Tag: tag,
                    FlowId: flow.FlowId));
            }
        }

        var nextSelected = SelectedTaskId ?? list.FirstOrDefault()?.Id;
        return this with
        {
            Tasks = [.. list],
            SelectedTaskId = nextSelected,
            NextTaskSequence = Math.Max(NextTaskSequence, list.Count + 1),
        };
    }

    [IgnoreExplore]
    public TasksWorkspaceState SyncDomainTasks(TaskRecord[] domainTasks)
    {
        var list = Tasks.ToList();
        var domainIds = new HashSet<string>(domainTasks.Select(static t => t.TaskId), StringComparer.Ordinal);
        // Remove stale domain tasks that were cleared in TasksNeuron (keep flow-projected items unless overwritten).
        list.RemoveAll(t => t.FlowId is null && !domainIds.Contains(t.Id));

        foreach (var task in domainTasks)
        {
            var stage = StageForTaskStatus(task.Status);
            var item = new TaskWorkItem(
                Id: task.TaskId,
                Title: task.Title,
                Stage: stage,
                Owner: task.Owner,
                Description: string.IsNullOrWhiteSpace(task.Summary) ? task.Description : task.Summary!,
                Tag: task.Tag,
                FlowId: task.FlowId);

            var existingIdx = list.FindIndex(t => string.Equals(t.Id, task.TaskId, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                list[existingIdx] = item;
            }
            else
            {
                list.Add(item);
            }
        }

        var nextSelected = SelectedTaskId is not null && list.Any(t => string.Equals(t.Id, SelectedTaskId, StringComparison.Ordinal))
            ? SelectedTaskId
            : list.FirstOrDefault()?.Id;
        return this with
        {
            Tasks = [.. list],
            SelectedTaskId = nextSelected,
            NextTaskSequence = Math.Max(NextTaskSequence, list.Count + 1),
        };
    }

    public static string StageForTaskStatus(TaskStatus status) => status switch
    {
        TaskStatus.InProgress => "in-progress",
        TaskStatus.NeedsYou or TaskStatus.Waiting => "review",
        TaskStatus.Completed or TaskStatus.Failed => "done",
        _ => "backlog",
    };

    public static TaskStatus TaskStatusForStage(string stage) => NormalizeStage(stage) switch
    {
        "in-progress" => TaskStatus.InProgress,
        "review" => TaskStatus.Waiting,
        "done" => TaskStatus.Completed,
        _ => TaskStatus.Queued,
    };

    [IgnoreExplore]
    public TasksWorkspaceState WithSource(string flowId, string? source)
        => this with { SelectedTaskId = flowId, Source = source };

    [IgnoreExplore]
    public TasksWorkspaceState WithStatus(string status)
        => this with { Status = status ?? string.Empty };

    [IgnoreExplore]
    public TasksWorkspaceState ApplyArrival(TasksNavArgs? args)
    {
        if (args is null)
        {
            return this;
        }

        var next = this;
        if (!string.IsNullOrWhiteSpace(args.FlowId))
        {
            next = next.SelectTaskOrFlow(args.FlowId);
        }

        if (!string.IsNullOrWhiteSpace(args.ConfirmationId))
        {
            next = next with { FocusedConfirmationId = args.ConfirmationId.Trim() };
        }

        if (!string.IsNullOrWhiteSpace(args.DraftPreviewId))
        {
            next = next with { FocusedDraftPreviewId = args.DraftPreviewId.Trim() };
        }

        if (!string.IsNullOrWhiteSpace(args.Tab))
        {
            next = next.SelectTab(args.Tab);
        }
        else if (!string.IsNullOrWhiteSpace(args.ConfirmationId) || !string.IsNullOrWhiteSpace(args.DraftPreviewId))
        {
            next = next.SelectTab("approvals");
        }

        return next;
    }

    [IgnoreExplore]
    public TasksWorkspaceState ApplyOpen(string? input, string? flowId, string? view)
    {
        var next = this;
        if (!string.IsNullOrWhiteSpace(flowId))
        {
            next = next.SelectTaskOrFlow(flowId);
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            var trimmed = input.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (lower is "board" or "kanban" or "table" or "list" or "approvals" or "confirmations" or "details")
            {
                next = next.SelectTab(lower);
            }
            else if (trimmed.StartsWith("confirm:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("confirmation:", StringComparison.OrdinalIgnoreCase))
            {
                var id = trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                next = (next with { FocusedConfirmationId = id.Length > 0 ? id : null }).SelectTab("approvals");
            }
            else if (trimmed.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
            {
                var id = trimmed["draft:".Length..].Trim();
                next = (next with { FocusedDraftPreviewId = id.Length > 0 ? id : null }).SelectTab("approvals");
            }
            else
            {
                next = next.SelectTaskOrFlow(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(view))
        {
            next = next.SelectTab(view);
        }

        return next;
    }

    [IgnoreExplore]
    public TaskWorkItem[] FilteredTasks(FlowView[] liveFlows)
    {
        var flowById = liveFlows.ToDictionary(f => f.FlowId, StringComparer.Ordinal);
        return Tasks.Where(t =>
        {
            if (Query.Owner is { } owner && !string.Equals(t.Owner, owner, StringComparison.Ordinal))
            {
                return false;
            }

            if (Query.Type is { } type)
            {
                if (t.FlowId is { } fid && flowById.TryGetValue(fid, out var fv))
                {
                    if (!string.Equals(fv.Type, type, StringComparison.Ordinal)) { return false; }
                }
                else if (!string.Equals(t.Tag, type, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (Query.Status is { } status)
            {
                if (t.FlowId is { } fid && flowById.TryGetValue(fid, out var fv))
                {
                    if (fv.Status != status) { return false; }
                }
                else if (!string.Equals(t.Stage, StageForFlowStatus(status), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }).ToArray();
    }

    public BoardColumn[] Columns(FlowView[]? liveFlows = null)
    {
        var visible = FilteredTasks(liveFlows ?? []);
        return
        [
            new("backlog", "Backlog", [.. visible.Where(static t => t.Stage == "backlog")]),
            new("in-progress", "In Progress", [.. visible.Where(static t => t.Stage == "in-progress")]),
            new("review", "Review", [.. visible.Where(static t => t.Stage == "review")]),
            new("done", "Done", [.. visible.Where(static t => t.Stage == "done")]),
        ];
    }

    // ── Normalization & Projection Utilities ─────────────────────────────────

    public static string NormalizeTab(string? tab) => (tab ?? "board").Trim().ToLowerInvariant() switch
    {
        "table" => "table",
        "list" => "list",
        "approvals" or "approval" or "confirmations" => "approvals",
        "details" or "detail" or "workflow" or "timeline" or "inspector" => "details",
        _ => "board",
    };

    public static string UiTabId(string? tab) => NormalizeTab(tab) switch
    {
        "table" or "list" => "table",
        "approvals" or "details" => "approvals",
        _ => "board",
    };

    public static string TabTitle(string tab) => NormalizeTab(tab) switch
    {
        "table" or "list" => "Task Table",
        "approvals" => "Approvals & Triage Drafts",
        "details" => "Workflow & Details",
        _ => "Kanban Board",
    };

    public static string? TryNormalizeStage(string? stage) => stage?.Trim().ToLowerInvariant() switch
    {
        "backlog" or "todo" or "pending" or "blocked" => "backlog",
        "in-progress" or "in_progress" or "inprogress" or "active" or "running" => "in-progress",
        "review" or "waiting" or "qa" or "verify" => "review",
        "done" or "completed" or "complete" or "closed" => "done",
        _ => null,
    };

    public static string NormalizeStage(string? stage) => TryNormalizeStage(stage) ?? "backlog";

    public static string NextStageOf(string stage) => NormalizeStage(stage) switch
    {
        "backlog" => "in-progress",
        "in-progress" => "review",
        _ => "done",
    };

    public static string StageTitle(string stage) => NormalizeStage(stage) switch
    {
        "in-progress" => "In Progress",
        "review" => "Review",
        "done" => "Done",
        _ => "Backlog",
    };

    public static string StageForFlowStatus(FlowStatus status) => status switch
    {
        FlowStatus.Running => "in-progress",
        FlowStatus.Waiting => "review",
        FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled => "done",
        _ => "backlog",
    };

    public static TimeSpan? ParseLag(string? text)
    {
        var value = text?.Trim().TrimEnd('d', 'D');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days >= 0
            ? TimeSpan.FromDays(days)
            : null;
    }

    public static TimelineBar[] Bars(FlowView[] flows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var byId = flows.ToDictionary(f => f.FlowId, StringComparer.Ordinal);
        var laid = new Dictionary<string, TimelineBar>(StringComparer.Ordinal);
        var laying = new HashSet<string>(StringComparer.Ordinal);

        TimelineBar Lay(FlowView flow)
        {
            if (laid.TryGetValue(flow.FlowId, out var done)) { return done; }
            laying.Add(flow.FlowId);
            var start = flow.Started;
            var end = flow.Ended ?? Projected(flow, now);
            foreach (var follows in flow.Relations.Where(r => r.Kind == RelationKind.Follows))
            {
                if (!byId.TryGetValue(follows.TargetFlowId, out var target) || laying.Contains(target.FlowId)) { continue; }
                var after = Lay(target).End + (follows.Lag ?? TimeSpan.Zero);
                if (after > start)
                {
                    end += after - start;
                    start = after;
                }
            }

            laying.Remove(flow.FlowId);
            return laid[flow.FlowId] = new TimelineBar(flow.FlowId, flow.Title, start, end);
        }

        return [.. flows.Select(Lay)];
    }

    public static string FirstLines(string text, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= count
            ? string.Join('\n', lines)
            : string.Join('\n', lines.Take(count)) + string.Create(CultureInfo.InvariantCulture, $"\n(+{lines.Length - count} more)");
    }

    private TasksWorkspaceState CreateTaskCore(string title, string description, string stage)
    {
        var candidateSeq = NextTaskSequence;
        var candidateId = string.Create(CultureInfo.InvariantCulture, $"task-{candidateSeq}");
        while (Tasks.Any(t => string.Equals(t.Id, candidateId, StringComparison.Ordinal)))
        {
            candidateSeq++;
            candidateId = string.Create(CultureInfo.InvariantCulture, $"task-{candidateSeq}");
        }

        var item = new TaskWorkItem(
            Id: candidateId,
            Title: title,
            Stage: stage,
            Owner: "owner",
            Description: description,
            Tag: "Task");

        return this with
        {
            Tasks = [.. Tasks, item],
            SelectedTaskId = candidateId,
            NextTaskSequence = candidateSeq + 1,
            Source = null,
            Draft = Draft with { Title = "", Description = "" },
            Status = $"Added '{title}' to {StageTitle(stage)}.",
        };
    }

    private static DateTimeOffset Projected(FlowView flow, DateTimeOffset now)
    {
        var elapsed = now > flow.Started ? now - flow.Started : TimeSpan.Zero;
        var span = TimeSpan.FromTicks((long)Math.Min(MaxProjection.Ticks, 2 * elapsed.Ticks / Math.Max(flow.Fraction, 0.05)));
        return flow.Started + span;
    }

    private static string ShortFlowType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) { return "Flow"; }
        var dot = type.LastIndexOf('.');
        var raw = dot >= 0 && dot + 1 < type.Length ? type[(dot + 1)..] : type;
        return char.ToUpperInvariant(raw[0]) + raw[1..];
    }
}
