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
    private async Task SyncUiNeuronsAsync(
        bool skipKanbanConfigure = false,
        bool skipStepperConfigure = false,
        bool skipTabsConfigure = false)
    {
        try
        {
            var state = State;
            var visible = state.FilteredTasks(_flows);

            if (CommandBar is { } cmdBar)
            {
                await cmdBar.Configure(
                [
                    new CommandBarItem("add-task", "Add Task", "plus", Disabled: state.BoardHasCapacity().Broken),
                    new CommandBarItem("advance-stage", "Advance Stage", "arrow-right", Disabled: state.CanAdvanceSelectedTask().Broken),
                    new CommandBarItem("complete-task", "Complete", "circle-check", Disabled: state.CanCompleteSelectedTask().Broken),
                    new CommandBarItem("clear-done", "Clear Done", "trash", Primary: false, Disabled: state.HasCompletedTasks().Broken),
                ]);
            }

            if (!skipTabsConfigure && Tabs is { } tabs)
            {
                var currentTabs = await tabs.Read();
                var targetUiTab = TasksWorkspaceState.UiTabId(state.ActiveTab);
                if (currentTabs.Version == 0 || !string.Equals(currentTabs.SelectedId, targetUiTab, StringComparison.Ordinal))
                {
                    var tabsState = await tabs.Configure(
                    [
                        new UiTabItem("board", "Board"),
                        new UiTabItem("table", "Table"),
                        new UiTabItem("approvals", "Approvals"),
                    ], targetUiTab);
                    _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, tabsState.Version);
                }
            }

            if (!skipStepperConfigure && Stepper is { } stepper)
            {
                var currentStepper = await stepper.Read();
                var steps = BuildStepperSteps(state);
                var activeStage = state.SelectedTask?.Stage ?? "backlog";
                var stepperTitle = state.SelectedTask is { } task
                    ? $"Lifecycle · {task.Title}"
                    : "Task Lifecycle";

                if (currentStepper.Version == 0
                    || !string.Equals(currentStepper.ActiveStepId, activeStage, StringComparison.Ordinal)
                    || !string.Equals(currentStepper.Title, stepperTitle, StringComparison.Ordinal)
                    || !StepperStepsMatch(currentStepper.Steps, steps))
                {
                    var stepperState = await stepper.Configure(steps, activeStage, stepperTitle);
                    _lastSyncedStepperVersion = Math.Max(_lastSyncedStepperVersion, stepperState.Version);
                }
            }

            if (!skipKanbanConfigure && Kanban is { } kanban)
            {
                var currentKanban = await kanban.Read();
                KanbanColumn[] cols =
                [
                    new("backlog", "Backlog", "list-todo"),
                    new("in-progress", "In Progress", "play"),
                    new("review", "Review", "eye"),
                    new("done", "Done", "circle-check"),
                ];
                var cards = BuildKanbanCards(visible);
                var selectedCardId = state.SelectedTaskId is { } sel && cards.Any(c => string.Equals(c.Id, sel, StringComparison.Ordinal))
                    ? sel
                    : cards.FirstOrDefault()?.Id;

                if (currentKanban.Version == 0 || !KanbanMatches(currentKanban, cards, selectedCardId))
                {
                    var kanbanState = await kanban.Configure("Tasks Board", cols, cards, selectedCardId);
                    _lastSyncedKanbanVersion = Math.Max(_lastSyncedKanbanVersion, kanbanState.Version);
                }
            }

            if (Table is { } table)
            {
                await table.Render(
                    "Workspace Tasks & Brain Flows",
                    [
                        new TableColumn("id", "ID"),
                        new TableColumn("title", "Title"),
                        new TableColumn("stage", "Stage"),
                        new TableColumn("owner", "Owner"),
                        new TableColumn("tag", "Kind"),
                    ],
                    visible.Select(static t => (IReadOnlyList<string>)
                    [
                        t.Id,
                        t.Title,
                        TasksWorkspaceState.StageTitle(t.Stage),
                        t.Owner,
                        t.Tag,
                    ]).ToArray());
            }
        }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogDebug(error, "Tasks UI neuron sync skipped in minimal test host.");
        }
    }

    private static StepperStep[] BuildStepperSteps(TasksWorkspaceState state)
    {
        var activeStage = state.SelectedTask?.Stage ?? "backlog";
        var activeIdx = Array.IndexOf(TasksWorkspaceState.StageIds, activeStage);
        if (activeIdx < 0) { activeIdx = 0; }

        return TasksWorkspaceState.StageIds.Select((stageId, idx) =>
        {
            var status = activeStage == "done"
                ? StepperStatuses.Done
                : idx < activeIdx
                    ? StepperStatuses.Done
                    : idx == activeIdx
                        ? StepperStatuses.Active
                        : StepperStatuses.Pending;
            return new StepperStep(stageId, TasksWorkspaceState.StageTitle(stageId), status);
        }).ToArray();
    }

    private static KanbanCard[] BuildKanbanCards(TaskWorkItem[] visible)
    {
        var list = new List<KanbanCard>(visible.Length);
        foreach (var stage in TasksWorkspaceState.StageIds)
        {
            var inStage = visible.Where(t => string.Equals(t.Stage, stage, StringComparison.Ordinal)).ToArray();
            for (var i = 0; i < inStage.Length; i++)
            {
                var item = inStage[i];
                var subtitle = string.IsNullOrWhiteSpace(item.Description) ? $"Owner: {item.Owner}" : item.Description;
                list.Add(new KanbanCard(
                    Id: item.Id,
                    ColumnId: stage,
                    Title: item.Title,
                    Subtitle: subtitle,
                    Tag: item.Tag,
                    Assignee: item.Owner,
                    Order: i));
            }
        }

        return [.. list];
    }

    private static bool StepperStepsMatch(StepperStep[] actual, StepperStep[] expected)
    {
        if (actual.Length != expected.Length) { return false; }
        for (var i = 0; i < actual.Length; i++)
        {
            if (!string.Equals(actual[i].Id, expected[i].Id, StringComparison.Ordinal)
                || !string.Equals(actual[i].Status, expected[i].Status, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool KanbanMatches(KanbanBoardState current, KanbanCard[] expectedCards, string? expectedSelectedId)
    {
        if (!string.Equals(current.SelectedCardId, expectedSelectedId, StringComparison.Ordinal)
            || current.Cards.Length != expectedCards.Length)
        {
            return false;
        }

        for (var i = 0; i < expectedCards.Length; i++)
        {
            var a = current.Cards[i];
            var b = expectedCards[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.ColumnId, b.ColumnId, StringComparison.Ordinal)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
