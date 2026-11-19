using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Tests;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Engineer;

public sealed class EngineerAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> EngineerOperations
        => ScreenBaseline.Cases(typeof(EngineerApp), os =>
        {
            os.Register(typeof(AppsModule).Assembly);
            os.Register(typeof(UiModule).Assembly);
        });

    [Theory, MemberData(nameof(EngineerOperations))]
    public async Task EngineerBaseline(ScreenOperationCase operation)
    {
        using var person = Edge.AsPerson();
        await ScreenBaseline.RunAsync(operation);
    }

    [Fact]
    public void EngineerWorkspaceAggregateGraphIsHealthyAndEnforcesStageRuleGuards()
    {
        var choices = new ChoiceSet()
            .ForParameter("tabId", "plan", "code", "verify", "pr")
            .ForParameter("stageId", "brainstorm-and-plan", "implement-tasks", "live-verify", "review-and-pr")
            .ForParameter("count", 1, 2)
            .ForParameter("backend", "LocalGit", "ContainerVolume")
            .ForParameter("goal", "Upgrade worktree orchestration");

        var graph = AggregateExplorer<EngineerWorkspaceState>.Explore(choices: choices, maxStates: 32);
        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.NotEmpty(graph.AcceptedTransitions);
        Assert.NotEmpty(graph.RejectedTransitions);

        var initial = new EngineerWorkspaceState();
        Assert.False(initial.PlanReadyToApprove().Broken);
        Assert.True(initial.ReadyForVerification().Broken);
        Assert.True(initial.ReadyForPrApproval().Broken);

        var approvedPlan = initial.ApprovePlan();
        Assert.True(approvedPlan.IsAccepted);
        Assert.False(approvedPlan.Next.ReadyForVerification().Broken);
        Assert.True(approvedPlan.Next.ReadyForPrApproval().Broken);

        var verified = approvedPlan.Next.CompleteVerification();
        Assert.True(verified.IsAccepted);
        Assert.False(verified.Next.ReadyForPrApproval().Broken);

        var published = verified.Next.ApproveAndPublishPr();
        Assert.True(published.IsAccepted);
        Assert.True(published.Next.PrPublished);
        Assert.True(published.Next.WorktreesCleaned);
        Assert.True(published.Next.HasActiveWorktrees().Broken);
    }

    [Fact]
    public async Task EngineerAppSynchronizesStepperTabsTreeEditorChatTableKanbanAndCommandBarOnTestOS()
    {
        var brain = new BrainId("engtest");
        await using var os = TestOS.Local();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(EngineerApp).Assembly);

        var screen = os.Get<IScreen>(new NeuronId(brain, "app.engineer", "default"));
        var initialView = await screen.Read();
        Assert.NotNull(initialView);

        var viewJson = System.Text.Json.JsonSerializer.Serialize(initialView);
        Assert.Contains(EngineerApp.StepperKey, viewJson, StringComparison.Ordinal);
        Assert.Contains(EngineerApp.CommandBarKey, viewJson, StringComparison.Ordinal);
        Assert.Contains(EngineerApp.WorktreeTreeKey, viewJson, StringComparison.Ordinal);
        Assert.Contains(EngineerApp.WorkspaceTabsKey, viewJson, StringComparison.Ordinal);
        Assert.Contains(EngineerApp.BrainstormChatKey, viewJson, StringComparison.Ordinal);

        var stepper = os.Get<IStepper>(new NeuronId(brain, "ui.stepper", EngineerApp.StepperKey));
        var tabs = os.Get<ITabs>(new NeuronId(brain, "ui.tabs", EngineerApp.WorkspaceTabsKey));
        var tree = os.Get<ITreeView>(new NeuronId(brain, "ui.tree-view", EngineerApp.WorktreeTreeKey));
        var editor = os.Get<ICodeEditor>(new NeuronId(brain, "ui.code-editor", EngineerApp.CodeEditorKey));
        var chat = os.Get<IChatThread>(new NeuronId(brain, "ui.chat", EngineerApp.BrainstormChatKey));
        var table = os.Get<ITable>(new NeuronId(brain, "ui.table", EngineerApp.TasksTableKey));
        var kanban = os.Get<IKanbanBoard>(new NeuronId(brain, "ui.kanban", EngineerApp.KanbanKey));
        var cmdBar = os.Get<ICommandBar>(new NeuronId(brain, "ui.command-bar", EngineerApp.CommandBarKey));

        Assert.Equal(6, (await stepper.Read()).Steps.Length);
        Assert.Equal("brainstorm-and-plan", (await stepper.Read()).ActiveStepId);
        Assert.Equal(4, (await tabs.Read()).Items.Length);
        Assert.True((await tree.Read()).Nodes.Length >= 4);
        Assert.True((await chat.Read()).Messages.Length >= 3);
        Assert.Equal(3, (await kanban.Read()).Columns.Length);
        Assert.Equal(6, (await cmdBar.Read()).Items.Length);

        // Invoke Approve Plan -> Run Verify -> Approve PR through live CommandBar neuron signals!
        await cmdBar.Invoke("approve-plan");
        await os.Idle();
        Assert.Equal("implement-tasks", (await stepper.Read()).ActiveStepId);
        Assert.Equal("code", (await tabs.Read()).SelectedId);
        Assert.Contains("EngineerFlow", (await editor.Read()).Content, StringComparison.Ordinal);

        await cmdBar.Invoke("run-verify");
        await os.Idle();
        Assert.Equal("review-and-pr", (await stepper.Read()).ActiveStepId);
        Assert.Equal("verify", (await tabs.Read()).SelectedId);
        Assert.True((await table.Read()).Total >= 4);

        await cmdBar.Invoke("approve-pr");
        await os.Idle();
        Assert.Equal("pr", (await tabs.Read()).SelectedId);
        Assert.All((await stepper.Read()).Steps, static s => Assert.Equal(StepperStatuses.Done, s.Status));
    }
}
