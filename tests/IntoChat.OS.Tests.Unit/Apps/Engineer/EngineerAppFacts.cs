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
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Engineer;

public sealed class EngineerAppFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
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

    [Fact]
    public async Task EngineerAppAutoDiscoversAndReflectsDelegatedEngineerFlowOnOpen()
    {
        var brain = new BrainId("engdeleg");
        var tempRoot = Path.Combine(Path.GetTempPath(), "eng-app-sync-" + Guid.NewGuid().ToString("N")[..8]);
        var bareDir = Path.Combine(tempRoot, "origin.git");
        var repoDir = Path.Combine(tempRoot, "repo");
        var wtHome = Path.Combine(tempRoot, "wt");
        Directory.CreateDirectory(bareDir);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(wtHome);

        try
        {
            RunGit(bareDir, ["init", "--bare", "--initial-branch=main"]);
            RunGit(repoDir, ["init", "--initial-branch=main"]);
            RunGit(repoDir, ["config", "user.name", "Owner"]);
            RunGit(repoDir, ["config", "user.email", "owner@example.test"]);
            Directory.CreateDirectory(Path.Combine(repoDir, "code"));
            await File.WriteAllTextAsync(Path.Combine(repoDir, "code", "Calc.cs"), "namespace Demo; public static class Calc { public static int Add(int a, int b) => a + b; }\n", Ct);
            RunGit(repoDir, ["add", "."]);
            RunGit(repoDir, ["commit", "-m", "init"]);
            RunGit(repoDir, ["remote", "add", "origin", bareDir]);
            RunGit(repoDir, ["push", "-u", "origin", "main"]);

            var wt = new IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.WorkTreeService(
                options: new IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.WorkTreeOptions
                {
                    HomeDirectory = wtHome,
                    VerifyFormatting = false,
                });
            wt.RegisterRepository(new IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.RegisteredRepository("os", repoDir, DefaultBaseBranch: "main"));

            var services = new ServiceCollection()
                .AddSingleton<IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.IWorkTree>(wt)
                .AddSingleton<IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.ICoder>(new TouchCalcCoder())
                .AddSingleton<IntoChat.OS.Modules.Apps.Flows.IEngineerPlanner>(new StubPlanner())
                .AddSingleton<IntoChat.OS.Modules.Apps.Flows.IEngineerBuildVerifier>(new StubBuildVerifier())
                .AddSingleton<IntoChat.OS.Modules.Apps.Flows.ILiveVerifier>(new StubLiveVerifier())
                .BuildServiceProvider();

            await using var os = TestOS.Local(o => o.Services = services);
            using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
            os.Register(typeof(AppsModule).Assembly);
            os.Register(typeof(UiModule).Assembly);
            os.Register(typeof(EngineerApp).Assembly);

            var flows = os.Get<IntoChat.OS.Modules.Apps.Flows.IFlows>(IntoChat.OS.Modules.Apps.Flows.IFlows.Key(brain.Value));
            var delegated = await flows.StartEngineerRun(
                new IntoChat.OS.Modules.Apps.Flows.EngineerDelegationRequest(
                    RepoSlug: "os",
                    Goal: "Add Subtract and Clamp helpers",
                    ParallelInstances: 2,
                    Backend: "LocalGit",
                    RunId: "syncrun01"),
                Ct);

            for (var i = 0; i < 60; i++)
            {
                await os.Idle();
                if ((await flows.Read(delegated.FlowId, Ct))?.Status == DigitalBrain.Flows.FlowStatus.Waiting)
                {
                    break;
                }

                await Task.Delay(40, Ct);
            }

            // Open EngineerApp WITHOUT passing FlowId: it must auto-discover syncrun01 and populate all UI neurons!
            var screen = os.Get<IScreen>(new NeuronId(brain, "app.engineer", "default"));
            await screen.Request(new EngineerOpen());
            await os.Idle();

            var stepper = os.Get<IStepper>(new NeuronId(brain, "ui.stepper", EngineerApp.StepperKey));
            var tree = os.Get<ITreeView>(new NeuronId(brain, "ui.tree-view", EngineerApp.WorktreeTreeKey));
            var kanban = os.Get<IKanbanBoard>(new NeuronId(brain, "ui.kanban", EngineerApp.KanbanKey));
            var tabs = os.Get<ITabs>(new NeuronId(brain, "ui.tabs", EngineerApp.WorkspaceTabsKey));

            Assert.Equal("review-and-pr", (await stepper.Read()).ActiveStepId);
            Assert.Equal("pr", (await tabs.Read()).SelectedId);
            var treeNodes = (await tree.Read()).Nodes;
            Assert.Contains(treeNodes, static n => n.Id == "wt-0" && n.Label.Contains("verified", StringComparison.Ordinal));
            Assert.Contains(treeNodes, static n => n.Id == "wt-1" && n.Label.Contains("verified", StringComparison.Ordinal));
            Assert.All((await kanban.Read()).Cards, static c => Assert.Equal("verified", c.ColumnId));

            await flows.Cancel(delegated.FlowId, "cleanup", Ct);
            await os.Idle();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    foreach (var f in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }

                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class TouchCalcCoder : IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.ICoder
    {
        public async Task<IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.CoderEditResult> EditAsync(
            IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.WorkTreeLease lease,
            string goal,
            CancellationToken ct = default)
        {
            var path = Path.Combine(lease.WorkspacePath, "code", "Calc.cs");
            await File.WriteAllTextAsync(
                path,
                $"namespace Demo;\npublic static class Calc {{ public static int Add(int a, int b) => a + b; public static int Sub{lease.InstanceIndex}(int a, int b) => a - b; }}\n",
                ct);
            return new IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.CoderEditResult("Updated Calc.cs", ["code/Calc.cs"], "test-coder");
        }
    }

    private sealed class StubPlanner : IntoChat.OS.Modules.Apps.Flows.IEngineerPlanner
    {
        public Task<IntoChat.OS.Modules.Apps.Flows.EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new IntoChat.OS.Modules.Apps.Flows.EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Stub plan",
                CriticReview: "Stub critic",
                Transcript:
                [
                    new("User", "user", "Goal", goal),
                    new("Architect", "architect", "Plan", "Edit code/Calc.cs"),
                    new("Critic", "critic", "Audit", "Zero warnings"),
                ],
                Tasks: [new("t1-sub", "Add Sub helper", "csharp", ["code/Calc.cs"], [], goal)]));
    }

    private sealed class StubBuildVerifier : IntoChat.OS.Modules.Apps.Flows.IEngineerBuildVerifier
    {
        public Task<IntoChat.OS.Modules.Apps.Flows.EngineerBuildReport> VerifyAsync(
            string worktreePath,
            IReadOnlyList<string> changedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new IntoChat.OS.Modules.Apps.Flows.EngineerBuildReport(true, true, true, true, 4, 0, 0, 0, [], 25, "Build OK"));
    }

    private sealed class StubLiveVerifier : IntoChat.OS.Modules.Apps.Flows.ILiveVerifier
    {
        public Task<IntoChat.OS.Modules.Apps.Flows.EngineerLiveReport> VerifyLiveAsync(
            string worktreePath,
            string goal,
            IReadOnlyList<string> changedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new IntoChat.OS.Modules.Apps.Flows.EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], ["span live.ok"], "Live OK"));
    }

    private static void RunGit(string workingDir, IReadOnlyList<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(proc.StandardError.ReadToEnd());
        }
    }
}
