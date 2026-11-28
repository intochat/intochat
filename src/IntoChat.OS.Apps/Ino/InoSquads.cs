using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Apps.Ino;

/// <summary>
/// Compact persisted summary of a live <see cref="IEngineerRuns"/> execution linked to a Squad thread.
/// </summary>
public sealed record InoEngineerRunSummary(
    string FlowId,
    string Title,
    string Status,
    string Phase,
    double Progress,
    string? Summary = null,
    string? Branch = null,
    int WorkerCount = 1);

/// <summary>
/// Validated creation plan for an <see cref="ThreadTrustPosture.OwnerTrusted"/> multi-agent Squad Room.
/// </summary>
public sealed record SquadCreationPlan(
    string Title,
    string[] SeatedAgentIds,
    string LeadAgentId);

/// <summary>
/// Result of validating a Squad Room creation request.
/// </summary>
public sealed record SquadValidationResult(
    bool Succeeded,
    SquadCreationPlan? Plan = null,
    string? Error = null);

/// <summary>
/// Pure policy, 5-stage Kanban/CheckBox projection, and UI helpers for Orca-style Multi-Agent Squad Rooms
/// and the Thread-Scoped Squad Planning &amp; Kanban Drawer.
/// </summary>
public static class InoSquads
{
    public const string DefaultLeadAgentId = "ino";
    public const string KanbanKey = "squad-plan-kanban";
    public const string CheckPrefix = "squad-chk-";
    public const string DrawerTabPlan = "plan";
    public const string DrawerTabPlanKey = "ino:drawer-tab-plan";
    public const string OpenPlanButtonKey = "ino:squad-open-plan";
    public const string AddTaskInputKey = "ino:squad-task-draft";
    public const string AddTaskButtonKey = "ino:squad-task-add";
    public const string ReturnToChoiceButtonKey = "ino:choice-return";

    /// <summary>Canonical 5-stage Squad Kanban columns: Planned -&gt; In Progress -&gt; Needs You -&gt; Waiting -&gt; Done.</summary>
    public static readonly KanbanColumnItem[] BoardColumns =
    [
        new("planned", "Planned", "list-todo"),
        new("in-progress", "In Progress", "play"),
        new("needs-you", "Needs You", "hand"),
        new("waiting", "Waiting", "clock"),
        new("done", "Done", "circle-check"),
    ];

    public static string CheckKey(string taskId) => CheckPrefix + taskId.Trim();

    public static bool TryTaskIdFromCheckKey(string? childKey, out string taskId)
    {
        taskId = string.Empty;
        if (string.IsNullOrWhiteSpace(childKey)
            || !childKey.StartsWith(CheckPrefix, StringComparison.Ordinal)
            || childKey.Length <= CheckPrefix.Length)
        {
            return false;
        }

        taskId = childKey[CheckPrefix.Length..];
        return true;
    }

    public static string ColumnIdForStatus(TaskStatus status) => status switch
    {
        TaskStatus.InProgress => "in-progress",
        TaskStatus.NeedsYou => "needs-you",
        TaskStatus.Waiting => "waiting",
        TaskStatus.Completed => "done",
        _ => "planned",
    };

    public static string ColumnIdForSlug(string? statusSlug) => (statusSlug ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "in-progress" or "inprogress" => "in-progress",
        "needs-you" or "needsyou" => "needs-you",
        "waiting" => "waiting",
        "completed" or "done" => "done",
        _ => "planned",
    };

    public static TaskStatus? TryTaskStatusForColumn(string? columnId) => (columnId ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "planned" or "queued" or "backlog" => TaskStatus.Queued,
        "in-progress" or "inprogress" => TaskStatus.InProgress,
        "needs-you" or "needsyou" => TaskStatus.NeedsYou,
        "waiting" => TaskStatus.Waiting,
        "done" or "completed" => TaskStatus.Completed,
        _ => null,
    };

    /// <summary>
    /// Validates a Squad Room creation request fail-closed:
    /// requires 2..N distinct seated agents, rejects any external friend / cross-brain account,
    /// and resolves a valid LeadAgentId (defaulting to <c>ino</c> when seated).
    /// </summary>
    public static SquadValidationResult PrepareCreate(
        string? rawTitle,
        string[]? rawAgentIds,
        string? rawLeadAgentId = null,
        string[]? externalAccounts = null)
    {
        if (externalAccounts is { Length: > 0 } && externalAccounts.Any(static a => !string.IsNullOrWhiteSpace(a)))
        {
            return new SquadValidationResult(
                false,
                Error: "External friends or cross-brain accounts cannot be seated in an OwnerTrusted Squad Room.");
        }

        var title = string.IsNullOrWhiteSpace(rawTitle) ? "New Squad" : rawTitle.Trim();
        if (title.Length > 120)
        {
            return new SquadValidationResult(false, Error: "Squad title must be 120 characters or fewer.");
        }

        var seated = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawAgentIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (LooksLikeExternalAccount(trimmed))
            {
                return new SquadValidationResult(
                    false,
                    Error: $"External account '{trimmed}' cannot be seated in an OwnerTrusted Squad Room.");
            }

            var normalized = NormalizeAgentToken(trimmed);
            if (string.IsNullOrEmpty(normalized))
            {
                return new SquadValidationResult(false, Error: $"Invalid seated agent identifier '{trimmed}'.");
            }

            if (seen.Add(normalized))
            {
                seated.Add(normalized);
            }
        }

        if (seated.Count < 2)
        {
            return new SquadValidationResult(
                false,
                Error: "A Squad Room requires at least 2 seated agents (for example @ino plus one or more custom specialists).");
        }

        string lead;
        if (!string.IsNullOrWhiteSpace(rawLeadAgentId))
        {
            var requestedLead = rawLeadAgentId.Trim();
            if (LooksLikeExternalAccount(requestedLead))
            {
                return new SquadValidationResult(
                    false,
                    Error: $"External account '{requestedLead}' cannot be configured as Squad lead.");
            }

            var normalizedLead = NormalizeAgentToken(requestedLead);
            var matchedLead = seated.FirstOrDefault(a => string.Equals(a, normalizedLead, StringComparison.OrdinalIgnoreCase));
            if (matchedLead is null)
            {
                return new SquadValidationResult(
                    false,
                    Error: $"Squad lead '{requestedLead}' must be one of the seated agents.");
            }

            lead = matchedLead;
        }
        else
        {
            lead = seated.FirstOrDefault(static a => string.Equals(a, DefaultLeadAgentId, StringComparison.OrdinalIgnoreCase))
                ?? seated[0];
        }

        return new SquadValidationResult(true, new SquadCreationPlan(title, [.. seated], lead));
    }

    public static bool LooksLikeExternalAccount(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("friend:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("person:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("account:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("brain:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("cross-brain:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Email-style external identity (contains '@' after index 0).
        var atIndex = value.IndexOf('@');
        return atIndex > 0;
    }

    public static string NormalizeAgentToken(string raw)
    {
        var token = raw.Trim().TrimStart('@').Trim();
        if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace) || token.Contains('/') || token.Contains('\\'))
        {
            return string.Empty;
        }

        return token.ToLowerInvariant();
    }

    /// <summary>Builds <see cref="KanbanProps"/> for the Squad Plan 5-column board backed by <c>KanbanNeuron</c>.</summary>
    public static KanbanProps BuildKanbanProps(
        string squadTitle,
        InoTaskSummary[] tasks,
        string? selectedTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var cards = new KanbanCardItem[tasks.Length];
        for (var i = 0; i < tasks.Length; i++)
        {
            var t = tasks[i];
            var col = ColumnIdForSlug(t.Status);
            var subtitle = !string.IsNullOrWhiteSpace(t.Summary)
                ? t.Summary
                : !string.IsNullOrWhiteSpace(t.AskQuestion)
                    ? $"Needs you: {t.AskQuestion}"
                    : t.TaskId;
            cards[i] = new KanbanCardItem(
                Id: t.TaskId,
                ColumnId: col,
                Title: t.Title,
                Subtitle: subtitle,
                Tag: t.Status);
        }

        var selected = !string.IsNullOrWhiteSpace(selectedTaskId)
            && cards.Any(c => string.Equals(c.Id, selectedTaskId, StringComparison.Ordinal))
            ? selectedTaskId
            : cards.FirstOrDefault()?.Id;

        var boardTitle = string.IsNullOrWhiteSpace(squadTitle) ? "Squad Plan" : $"{squadTitle} · Plan";
        return new KanbanProps(boardTitle, BoardColumns, cards, SelectedCardId: selected, AutomationName: "Squad Plan Kanban");
    }

    /// <summary>Projects a live <see cref="EngineerRunSummary"/> into a compact persisted <see cref="InoEngineerRunSummary"/>.</summary>
    public static InoEngineerRunSummary FromEngineerRun(EngineerRunSummary run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var normalizedStatus = run.Status.ToString().ToLowerInvariant();
        var phase = string.IsNullOrWhiteSpace(run.CurrentStage) ? normalizedStatus : run.CurrentStage.Trim();
        var progress = normalizedStatus switch
        {
            "completed" or "done" or "succeeded" => 1.0,
            "failed" or "cancelled" or "error" => 1.0,
            _ => phase.ToLowerInvariant() switch
            {
                "plan" or "planning" => 0.2,
                "implement" or "running" or "executing" => 0.55,
                "verify" or "verifying" or "self-review" => 0.8,
                "approval" or "awaiting-approval" => 0.9,
                _ => 0.45,
            },
        };
        var workerCount = Math.Max(1, Math.Max(run.ParallelInstances, run.Instances?.Length ?? 0));
        var branch = !string.IsNullOrWhiteSpace(run.PushedRef) ? run.PushedRef : null;
        var summary = !string.IsNullOrWhiteSpace(run.SummaryText)
            ? run.SummaryText
            : $"{workerCount} worker(s) · {phase}";
        return new InoEngineerRunSummary(
            FlowId: run.FlowId,
            Title: string.IsNullOrWhiteSpace(run.Goal) ? $"Engineer Run {run.FlowId}" : run.Goal,
            Status: normalizedStatus,
            Phase: phase,
            Progress: progress,
            Summary: summary,
            Branch: branch,
            WorkerCount: workerCount);
    }

    /// <summary>Renders an inline <see cref="JobCardNode"/> reflecting parallel worker / <see cref="IEngineerRuns"/> progress.</summary>
    public static JobCardNode RenderEngineerRunJobCard(InoEngineerRunSummary run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (jobStatus, stepStatus) = run.Status.ToLowerInvariant() switch
        {
            "completed" or "done" or "succeeded" => ("done", "done"),
            "failed" or "error" or "cancelled" => ("error", "failed"),
            _ => ("running", "running"),
        };

        var workerDetail = run.WorkerCount > 1
            ? $"{run.WorkerCount} parallel workers · {run.Phase}"
            : run.Phase;
        var steps = new JobStepNode[]
        {
            new("Coordinate squad plan", "done"),
            new(workerDetail, stepStatus, run.Branch ?? run.Summary),
        };

        var openButton = Button("Open artifact", "external-link")
            .Small()
            .Key($"open-squad-run:{run.FlowId}")
            .Sends(new InoOpenDrawer(
                ArtifactId: run.FlowId,
                FlowId: run.FlowId,
                Title: run.Title,
                Kind: "engineer-run"))
            .ToButton();

        return new JobCardNode(
            run.Title,
            steps,
            Progress: run.Progress,
            Status: jobStatus,
            ResultText: run.Summary,
            OpenArtifact: openButton);
    }

    /// <summary>Renders the Squad Room header bar with <c>OwnerTrusted · Squad</c> badge, seated agent chips, and Plan Drawer trigger.</summary>
    public static Node RenderSquadHeader(InoAppState s, ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(view);

        var seatedChips = view.SeatedAgents
            .Select(agent =>
            {
                var handle = string.IsNullOrWhiteSpace(agent.Handle) ? "@" + agent.AgentId : agent.Handle;
                var chipLabel = agent.IsLead ? $"{handle} · Lead" : handle;
                return Button(chipLabel)
                    .Small()
                    .Key($"squad-chip:{agent.AgentId}")
                    .Style(OsMixStyles.FilterPill(selected: agent.IsLead))
                    .Sends(new InoSend(Message: $"{handle} status update"))
                    .ToNode();
            })
            .ToArray();

        var headerRows = new List<Node>(3)
        {
            HStack(Space.Sm)[
                Heading(string.IsNullOrWhiteSpace(view.Title) ? s.Title : view.Title, 3),
                StatusPill(StatusKind.Done, "OwnerTrusted · Squad"),
                PrimaryButton("Squad Plan", "kanban")
                    .Small()
                    .Key(OpenPlanButtonKey)
                    .Sends(new InoOpenSquadPlan())
                    .ToNode()
            ],
        };

        if (seatedChips.Length > 0)
        {
            headerRows.Add(HStack(Space.Xs, seatedChips));
        }

        if (!string.IsNullOrWhiteSpace(view.HandoffLimitNotice))
        {
            headerRows.Add(Banner("Delegation limit reached", view.HandoffLimitNotice, tone: "warning"));
        }

        return Surface(SurfaceKind.Acrylic)
            .Style(Mix.Style().Tone(UiTone.Elevated).Padding(12).Radius(14))[
            VStack(Space.Xs, [.. headerRows])
        ];
    }

    /// <summary>
    /// Renders the Thread-Scoped Squad Planning &amp; Kanban Drawer body backed by server-side
    /// <see cref="IKanbanControl"/> (<c>KanbanNeuron</c>) and <see cref="ICheckBoxControl"/> (<c>CheckBoxNeuron</c>) grains.
    /// </summary>
    public static Node RenderSquadPlanBody(
        InoAppState s,
        InoTaskSummary[] tasks,
        Func<string, KanbanProps, Node> kanban,
        Func<string, CheckBoxProps, Node> checkBox)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(kanban);
        ArgumentNullException.ThrowIfNull(checkBox);

        var kanbanNode = kanban(KanbanKey, BuildKanbanProps(s.Title, tasks, s.SelectedSquadTaskId));

        Node[] checklistRows = tasks.Length == 0
            ? [Empty("No squad tasks yet", "Add a task or let a seated agent create one on the Squad Plan board.")]
            : [.. tasks.Select(task =>
            {
                var isDone = string.Equals(ColumnIdForSlug(task.Status), "done", StringComparison.Ordinal);
                return (Node)HStack(Space.Sm)[
                    checkBox(CheckKey(task.TaskId), new CheckBoxProps(task.Title, IsOn: isDone)),
                    StatusPill(
                        isDone ? StatusKind.Done
                            : string.Equals(task.Status, "needs-you", StringComparison.OrdinalIgnoreCase) ? StatusKind.NeedsYou
                            : StatusKind.Working,
                        task.Status)
                ];
            })];

        var completedCount = tasks.Count(static t => string.Equals(ColumnIdForSlug(t.Status), "done", StringComparison.Ordinal));
        var checklistCard = Card("Squad Checklist", $"{completedCount}/{tasks.Length} done · synced with KanbanNeuron & CheckBoxNeuron")
            .Style(OsMixStyles.Card())[
            VStack(Space.Xs, checklistRows)
        ];

        return VStack(Space.Sm)[
            kanbanNode,
            checklistCard
        ];
    }

    /// <summary>Renders the subtle bar shown while the user is chatting about a pending choice without losing the pending choice state.</summary>
    public static Node? RenderChatAboutChoiceBanner(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(s.ChattingAboutChoiceTaskId))
        {
            return null;
        }

        var task = s.ActiveTasks.FirstOrDefault(t =>
            string.Equals(t.TaskId, s.ChattingAboutChoiceTaskId, StringComparison.Ordinal)
            && string.Equals(t.Status, "needs-you", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(t.AskQuestion));
        if (task is null)
        {
            return null;
        }

        return Surface(SurfaceKind.Acrylic)
            .Style(Mix.Style().Tone(UiTone.AccentTint).Padding(10).Radius(12))[
            HStack(Space.Sm)[
                StatusPill(StatusKind.NeedsYou, "Choice waiting"),
                MutedText(task.AskQuestion!),
                PrimaryButton("Return to choice", "corner-up-left")
                    .Small()
                    .Key(ReturnToChoiceButtonKey)
                    .Sends(new InoChatAboutChoice(task.TaskId, Active: false))
                    .ToNode()
            ]
        ];
    }
}
