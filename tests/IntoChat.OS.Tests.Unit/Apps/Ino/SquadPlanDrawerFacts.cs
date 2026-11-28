using System.Text.Json;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Unit.Apps.Ino;

public sealed class SquadPlanDrawerFacts
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [Fact]
    public void SquadPlanKanban_UsesCanonicalFiveStageColumns_AndMapsTaskStatuses()
    {
        InoTaskSummary[] tasks =
        [
            new("task-1", "Draft RFC", "queued", Summary: "Waiting to start"),
            new("task-2", "Build API", "in-progress", Summary: "Coder working"),
            new("task-3", "Pick auth provider", "needs-you", AskQuestion: "Which OAuth provider?", AskOptions: ["GitHub", "Google", "Okta"], AskRecommendedIndex: 0),
            new("task-4", "Wait for CI", "waiting", Summary: "GitHub Actions running"),
            new("task-5", "Ship release", "completed", Summary: "Merged"),
        ];

        var props = InoSquads.BuildKanbanProps("Release Squad", tasks, selectedTaskId: "task-2");

        Assert.Equal(["planned", "in-progress", "needs-you", "waiting", "done"], props.Columns.Select(static c => c.Id));
        Assert.Equal(["Planned", "In Progress", "Needs You", "Waiting", "Done"], props.Columns.Select(static c => c.Title));
        Assert.Equal("task-2", props.SelectedCardId);
        Assert.Equal(5, props.Cards.Count);
        Assert.Equal("planned", props.Cards[0].ColumnId);
        Assert.Equal("in-progress", props.Cards[1].ColumnId);
        Assert.Equal("needs-you", props.Cards[2].ColumnId);
        Assert.Equal("waiting", props.Cards[3].ColumnId);
        Assert.Equal("done", props.Cards[4].ColumnId);
    }

    [Fact]
    public void ScreenNode_RendersSquadHeader_PlanDrawerWithKanbanAndCheckBoxes_EngineerJobCard_AndDockedChoiceCard()
    {
        var view = new ConversationView(
            ConversationId.Parse("squad-abc123"),
            Revision: 2,
            Participants: [new Participant.Assistant("ino"), new Participant.Assistant("agent-architect")],
            Entries: [],
            Active: null,
            Runs: [],
            Blocked: false,
            Contexts: [])
        {
            TrustPosture = ThreadTrustPosture.OwnerTrusted,
            Kind = ThreadKind.Squad,
            Title = "Architecture Squad",
            LeadAgentId = "ino",
            SeatedAgents =
            [
                new SeatedAgentSummary("ino", "Ino", "@ino", IsLead: true),
                new SeatedAgentSummary("agent-architect", "Architect", "@architect", "memory/bots/architect", IsLead: false),
            ],
            HandoffLimitNotice = "Squad delegation limit reached (maxDepth = 3): halted handoff from @architect to @ino.",
        };

        var engineerRun = new InoEngineerRunSummary(
            FlowId: "eng-42",
            Title: "Implement B5 Squad Rooms",
            Status: "running",
            Phase: "implement",
            Progress: 0.55,
            Summary: "2 parallel workers · implement",
            Branch: "feat/squad-rooms",
            WorkerCount: 2);

        var state = new InoAppState
        {
            ConversationId = "squad-abc123",
            Title = "Architecture Squad",
            DrawerOpen = true,
            DrawerTab = InoSquads.DrawerTabPlan,
            ActiveTasks =
            [
                new InoTaskSummary("task-1", "Approve schema", "needs-you", AskQuestion: "Which storage layout?", AskOptions: ["Option A", "Option B", "Option C"], AskRecommendedIndex: 0),
                new InoTaskSummary("task-2", "Write tests", "completed", Summary: "All green"),
            ],
            SquadEngineerRuns = [engineerRun],
        };

        var mountedKanban = new List<(string Key, KanbanProps Props)>();
        var mountedChecks = new List<(string Key, CheckBoxProps Props)>();
        ChatThreadProps? capturedThreadProps = null;

        var root = InoView.ScreenNode(
            state,
            VoiceAbilities.None,
            props =>
            {
                capturedThreadProps = props;
                return new Spacer(0);
            },
            view: view,
            kanban: (key, props) =>
            {
                mountedKanban.Add((key, props));
                return new Spacer(0);
            },
            checkBox: (key, props) =>
            {
                mountedChecks.Add((key, props));
                return new Spacer(0);
            });

        // Right SplitPane Drawer mounted with server-side KanbanNeuron & CheckBoxNeuron props.
        Assert.IsType<SplitPane>(root);
        var (kanbanKey, kanbanProps) = Assert.Single(mountedKanban);
        Assert.Equal(InoSquads.KanbanKey, kanbanKey);
        Assert.Equal(5, kanbanProps.Columns.Count);
        Assert.Equal(2, kanbanProps.Cards.Count);

        Assert.Equal(2, mountedChecks.Count);
        Assert.Contains(mountedChecks, c => c.Key == InoSquads.CheckKey("task-1") && c.Props.IsOn == false);
        Assert.Contains(mountedChecks, c => c.Key == InoSquads.CheckKey("task-2") && c.Props.IsOn == true);

        // Docked ChoiceCardNode with 1..9 options.
        Assert.NotNull(capturedThreadProps);
        var choice = Assert.IsType<ChoiceCardNode>(capturedThreadProps!.Waiting);
        Assert.Equal("Which storage layout?", choice.Question);
        Assert.Equal(3, choice.Options.Count);
        Assert.Equal("Recommended", choice.Options[0].Detail);

        // Inline JobCardNode for parallel IEngineerRuns progress and Squad header chips.
        var rootJson = JsonSerializer.Serialize(root, WebJson);
        Assert.Contains("Implement B5 Squad Rooms", rootJson, StringComparison.Ordinal);
        Assert.Contains("2 parallel workers", rootJson, StringComparison.Ordinal);
        Assert.Contains("OwnerTrusted", rootJson, StringComparison.Ordinal);
        Assert.Contains("@architect", rootJson, StringComparison.Ordinal);

        // Chat about this: releases composer without losing the pending choice state.
        var chattingState = state with { ChattingAboutChoiceTaskId = "task-1" };
        ChatThreadProps? chattingThreadProps = null;
        var chattingRoot = InoView.ScreenNode(
            chattingState,
            VoiceAbilities.None,
            props =>
            {
                chattingThreadProps = props;
                return new Spacer(0);
            },
            view: view);

        Assert.Null(chattingThreadProps!.Waiting);
        Assert.Contains(chattingState.ActiveTasks, t => t.TaskId == "task-1" && t.Status == "needs-you" && t.AskQuestion == "Which storage layout?");
        var chattingJson = JsonSerializer.Serialize(chattingRoot, WebJson);
        Assert.Contains(InoSquads.ReturnToChoiceButtonKey, chattingJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PassiveViewRender_DoesNotMutateInoAppState()
    {
        var state = new InoAppState
        {
            ConversationId = "squad-passive",
            Title = "Passive Squad",
            DrawerOpen = true,
            DrawerTab = InoSquads.DrawerTabPlan,
            ActiveTasks = [new InoTaskSummary("task-1", "Check", "queued")],
        };
        var snapshotBefore = state with { };

        _ = InoView.ScreenNode(state, VoiceAbilities.None, static _ => new Spacer(0));

        Assert.Equal(snapshotBefore.ConversationId, state.ConversationId);
        Assert.Equal(snapshotBefore.DrawerOpen, state.DrawerOpen);
        Assert.Equal(snapshotBefore.DrawerTab, state.DrawerTab);
        Assert.Equal(snapshotBefore.ActiveTasks.Length, state.ActiveTasks.Length);
    }
}
