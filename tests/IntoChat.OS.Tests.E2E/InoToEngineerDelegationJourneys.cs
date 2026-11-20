using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using IntoChat.OS.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.E2E;

/// <summary>
/// Live in-process OS end-to-end journey proving <c>app.ino</c> -&gt; <c>flow.engineer</c> -&gt; <c>app.engineer</c> delegation:
/// 1. User/Ino invokes the Engineer delegation tool (<c>ParallelInstances = 2</c>, <c>ContainerVolume</c>) on a real Git repo + bare remote.
/// 2. Live multi-agent Architect + Critic planning refines a multi-task <see cref="EngineerTaskPlan"/>, Roslyn AST edits compile in Release
///    across both parallel worktrees, and <see cref="ILiveVerifier"/> verifies OpenTelemetry spans, <c>ITraceReader</c>, and <see cref="IScreen"/> health
///    before parking at <c>approval-gate</c>.
/// 3. <see cref="EngineerApp"/> (<c>app.engineer</c>) opened in the same brain immediately reflects the delegated <c>flow.engineer</c> run state,
///    stages, brainstorm turns, tasks, telemetry rows, and parallel worktree leases across its <see cref="IStepper"/>, <see cref="ITreeView"/>,
///    <see cref="IKanbanBoard"/>, <see cref="ITable"/>, <see cref="IChatThread"/>, and <see cref="ITabs"/>.
/// 4. Human owner approval via <see cref="IConfirmationInbox"/> pushes the verified commit SHA to bare <c>origin</c> and cleans up both worktrees.
/// </summary>
public sealed class InoToEngineerDelegationJourneys(TestOSFixture fixture) : E2ETestBase, IClassFixture<TestOSFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InoDelegatesTwoWorktreeEngineerRunWithArchitectCriticPlanningLiveTelemetryAndOwnerApproval()
    {
        using var tempHome = new TestHome();
        try
        {
            await ExecuteDelegationJourneyAsync(tempHome.Path);
        }
        finally
        {
            ClearReadOnlyAttributes(tempHome.Path);
        }
    }

    private async Task ExecuteDelegationJourneyAsync(string homeRoot)
    {
        var (bareOrigin, sourceRepo, workTreeHome, volumeRoot) = SeedCompilableGitRepository(homeRoot);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Engineer:RunTestSuites"] = "false",
            })
            .Build();

        var workTree = new WorkTreeService(
            configuration: configuration,
            options: new WorkTreeOptions
            {
                HomeDirectory = workTreeHome,
                VolumeMountRoot = volumeRoot,
                Backend = WorkTreeBackendKind.ContainerVolume,
                VerifyFormatting = false,
            });
        workTree.RegisterRepository(new RegisteredRepository(
            Id: "os",
            LocalPath: sourceRepo,
            DefaultBaseBranch: "main",
            RemoteCloneUrl: bareOrigin));

        var plannerPrompts = new List<string>();
        var inference = new ArchitectCriticModelInference(plannerPrompts);
        var roslyn = IWorktreeRoslyn.CreateDefault();
        var coder = new DelegatedRoslynCoder(roslyn);
        using var liveVerifier = new EngineerLiveVerifier(configuration);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IModelInference>(inference);
        serviceCollection.AddSingleton<IEngineerPlanner>(sp => new EngineerPlanner(sp));
        serviceCollection.AddSingleton<ISandboxCommandRunner>(_ => ISandboxCommandRunner.CreateDefault(configuration: configuration));
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(sp =>
            new EngineerBuildVerifier(configuration, sp.GetRequiredService<ISandboxCommandRunner>()));
        serviceCollection.AddSingleton<ILiveVerifier>(liveVerifier);
        serviceCollection.AddSingleton(roslyn);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = fixture.CreateIsolated(o => o.Services = services);
        var brain = NewBrain("inodeleg");
        using var owner = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(InoApp).Assembly);

        // Verify Ino's live screen and agent persona expose the Engineer delegation handoff.
        var inoScreen = os.Get<IScreen>(new NeuronId(brain, "app.ino", "default"));
        Assert.NotNull(await inoScreen.Read());
        var personaDef = InoAgentDefinition.For(new UserSettingsState() with { DisplayName = "Owner", DefaultModel = "qwen2.5-coder:14b" });
        Assert.Contains(typeof(IFlows).FullName!, personaDef.Contracts);
        Assert.Contains("`flows_start_engineer_run`", personaDef.Instructions, StringComparison.Ordinal);

        // 1. Ino delegates a 2-worktree ContainerVolume IntoChat-Engineer run on behalf of the user.
        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var studio = os.Get<IStudio>(new NeuronId(brain, "ino.studio", "ino/studio"));
        EngineerRunSummary started;
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), brain, conversation: "main")))
        {
            started = await flows.StartEngineerRun(
                new EngineerDelegationRequest(
                    RepoSlug: "os",
                    Goal: "Add Subtract and Clamp helpers to Calculator",
                    ParallelInstances: 2,
                    Backend: "ContainerVolume",
                    RunId: "deleg01"),
                Ct);
        }

        Assert.True(started.Ok, started.SummaryText);
        Assert.Equal($"{brain.Value}/{IFlows.EngineerFlow}/deleg01", started.FlowId);

        // 2. Drive flow.engineer through Architect+Critic planning -> 2 parallel worktrees -> Roslyn AST edits -> Release build -> OTel/Trace/IScreen live-verify -> approval-gate.
        EngineerRunSummary? waiting = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await flows.InspectEngineerRun(started.FlowId, Ct);
            if (snapshot.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed)
            {
                waiting = snapshot;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(waiting);
        Assert.True(
            waiting!.Status == FlowStatus.Waiting,
            $"Expected delegated flow.engineer run to park at approval-gate, got {waiting.Status}: {waiting.SummaryText}");
        Assert.Equal("approval-gate", waiting.CurrentStage);
        Assert.Equal(2, waiting.ParallelInstances);
        Assert.Equal("ContainerVolume", waiting.Backend);
        Assert.NotNull(waiting.ConfirmationPlanId);
        Assert.StartsWith("app:engineer:deleg01:", waiting.ConfirmationPlanId!, StringComparison.Ordinal);
        Assert.Contains($"neuron://{brain.Value}/apps/engineer#{started.FlowId}", waiting.EngineerAppUri, StringComparison.Ordinal);

        // Verify distinct Architect and Critic model turns executed and refined the 2-task DAG.
        Assert.Equal(2, plannerPrompts.Count);
        Assert.Contains("ARCHITECT", plannerPrompts[0], StringComparison.Ordinal);
        Assert.Contains("CRITIC", plannerPrompts[1], StringComparison.Ordinal);
        Assert.Contains("Architect initial decomposition", plannerPrompts[1], StringComparison.Ordinal);

        Assert.NotNull(waiting.Plan);
        Assert.Equal(["t1-subtract", "t2-clamp"], waiting.Plan!.TopologicalOrder().Select(static t => t.Id));
        Assert.Contains(waiting.Plan.Transcript, static t => t.Role == "architect");
        Assert.Contains(waiting.Plan.Transcript, static t => t.Role == "critic");

        // Verify Ino's tool result card extraction & IStudio.ChangeStatus report the pending approval card cleanly.
        var toolResultJson = JsonSerializer.Serialize(waiting, JsonSerializerOptions.Web);
        var projectedCard = Assert.Single(InoCards.FromToolCalls(
        [
            new AgentToolCall("call-1", "neurons_call", "{}", toolResultJson, null),
        ]));
        Assert.Equal("approval", projectedCard.Kind);
        Assert.True(projectedCard.IsWaiting);

        var studioReport = await studio.ChangeStatus(started.FlowId, Ct);
        Assert.True(studioReport.Ok, studioReport.Text);
        Assert.NotNull(studioReport.Card);
        Assert.Equal("approval", studioReport.Card!.Kind);

        // Verify ILiveVerifier audited real OpenTelemetry spans, causal ITraceReader journal signals, and live IScreen health!
        var starter = os.Get<IFlowStarter>(NeuronId.Parse(started.FlowId));
        var headAtGate = await starter.Head();
        using (var gateDoc = JsonDocument.Parse(headAtGate.InnerJson))
        {
            var instances = gateDoc.RootElement.GetProperty("instances");
            Assert.Equal(2, instances.GetArrayLength());
            for (var idx = 0; idx < 2; idx++)
            {
                var inst = instances[idx];
                Assert.Equal("verified", inst.GetProperty("status").GetString());
                Assert.True(inst.GetProperty("build").GetProperty("succeeded").GetBoolean());
                var live = inst.GetProperty("live");
                Assert.True(live.GetProperty("succeeded").GetBoolean());
                Assert.Equal(0, live.GetProperty("errorSpanCount").GetInt32());
                Assert.Equal(0, live.GetProperty("errorLogCount").GetInt32());

                var probeNames = live.GetProperty("probes")
                    .EnumerateArray()
                    .Select(static p => (Name: p.GetProperty("name").GetString()!, Passed: p.GetProperty("passed").GetBoolean()))
                    .ToArray();
                Assert.Contains(probeNames, static p => p.Name == "otel-activity-spans" && p.Passed);
                Assert.Contains(probeNames, static p => p.Name == "brain-trace-reader" && p.Passed);
                Assert.Contains(probeNames, static p => p.Name == "mcp-screen-app.engineer" && p.Passed);

                var leaseTree = inst.GetProperty("lease").GetProperty("treePath").GetString()!;
                Assert.True(Directory.Exists(leaseTree), $"Worktree #{idx} must remain intact while parked at approval-gate.");
            }
        }

        // Nothing is pushed to bare origin before human approval.
        Assert.Equal(string.Empty, RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim());

        // 3. Open EngineerApp (app.engineer) in the SAME brain without manual FlowId seeding: it must reflect the delegated run!
        var engineerScreen = os.Get<IScreen>(new NeuronId(brain, "app.engineer", "default"));
        await engineerScreen.Request(new EngineerOpen());
        await os.Idle();

        var engStepper = os.Get<IStepper>(new NeuronId(brain, "ui.stepper", EngineerApp.StepperKey));
        var engTree = os.Get<ITreeView>(new NeuronId(brain, "ui.tree-view", EngineerApp.WorktreeTreeKey));
        var engKanban = os.Get<IKanbanBoard>(new NeuronId(brain, "ui.kanban", EngineerApp.KanbanKey));
        var engTable = os.Get<ITable>(new NeuronId(brain, "ui.table", EngineerApp.TasksTableKey));
        var engTabs = os.Get<ITabs>(new NeuronId(brain, "ui.tabs", EngineerApp.WorkspaceTabsKey));
        var engChat = os.Get<IChatThread>(new NeuronId(brain, "ui.chat", EngineerApp.BrainstormChatKey));

        Assert.Equal("review-and-pr", (await engStepper.Read()).ActiveStepId);
        Assert.Equal("pr", (await engTabs.Read()).SelectedId);

        var treeNodes = (await engTree.Read()).Nodes;
        Assert.Contains(treeNodes, static n => n.Id == "wt-0" && n.Label.Contains("ContainerVolume", StringComparison.Ordinal) && n.Label.Contains("verified", StringComparison.Ordinal));
        Assert.Contains(treeNodes, static n => n.Id == "wt-1" && n.Label.Contains("ContainerVolume", StringComparison.Ordinal) && n.Label.Contains("verified", StringComparison.Ordinal));
        Assert.Contains(treeNodes, static n => n.Label == "Calculator.cs");

        var kanbanState = await engKanban.Read();
        Assert.Equal(["t1-subtract", "t2-clamp"], kanbanState.Cards.Select(static c => c.Id));
        Assert.All(kanbanState.Cards, static c => Assert.Equal("verified", c.ColumnId));

        Assert.True((await engChat.Read()).Messages.Length >= 3);

        // Switch EngineerApp to the Verification & Traces tab and verify real OTel/trace entries populate ITable!
        await engineerScreen.Request(new EngineerSelectTab("verify"));
        await os.Idle();
        var verifyTable = await engTable.Read();
        Assert.True(verifyTable.Total >= 4);

        // 4. Approve via IConfirmationInbox as the human owner: push verified commit SHA to bare origin & clean up both worktrees!
        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var pendingItem = Assert.Single((await inbox.Read()).Pending, p => p.PlanId == waiting.ConfirmationPlanId);
        Assert.Equal(EngineerFlow.ConfirmationKind, pendingItem.Plan.Kind);
        await inbox.Resolve(waiting.ConfirmationPlanId!, approve: true);

        EngineerRunSummary? completed = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await flows.InspectEngineerRun(started.FlowId, Ct);
            if (snapshot.Status is FlowStatus.Completed or FlowStatus.Failed)
            {
                completed = snapshot;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(completed);
        Assert.True(
            completed!.Status == FlowStatus.Completed,
            $"Expected delegated flow.engineer run to complete after owner approval, got {completed.Status}: {completed.SummaryText}");
        Assert.Equal(EngineerFlow.Names, completed.CompletedStages);
        Assert.NotNull(completed.Card);
        Assert.Equal("engineer", completed.Card!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(completed.WinningCommitSha));
        Assert.False(string.IsNullOrWhiteSpace(completed.PushedRef));

        // Verify bare remote received the exact SHA-bound winning commit with both Roslyn-authored methods.
        var remoteCommitSha = RunGit(bareOrigin, ["rev-parse", completed.PushedRef!]).Trim();
        Assert.Equal(completed.WinningCommitSha, remoteCommitSha);

        var committedSource = RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Calculator.cs"]);
        Assert.Contains("public static int Subtract(int a, int b)", committedSource, StringComparison.Ordinal);
        Assert.Contains("public static int Clamp(int value, int min, int max)", committedSource, StringComparison.Ordinal);

        // Verify both parallel ContainerVolume worktree directories were cleaned up.
        using (var afterDoc = JsonDocument.Parse((await starter.Head()).InnerJson))
        {
            foreach (var inst in afterDoc.RootElement.GetProperty("instances").EnumerateArray())
            {
                var treePath = inst.GetProperty("lease").GetProperty("treePath").GetString()!;
                Assert.False(Directory.Exists(treePath), $"Worktree '{treePath}' must be cleaned up after publish.");
            }
        }

        // Verify EngineerApp stepper reflects all 6 stages Done after completion.
        await engineerScreen.Request(new EngineerOpen(FlowId: started.FlowId));
        await os.Idle();
        Assert.All((await engStepper.Read()).Steps, static step => Assert.Equal(StepperStatuses.Done, step.Status));
        Assert.Empty((await inbox.Read()).Pending);
    }

    private static (string BareOrigin, string SourceRepo, string WorkTreeHome, string VolumeRoot) SeedCompilableGitRepository(string baseDir)
    {
        var bareOrigin = Path.Combine(baseDir, "origin.git");
        var sourceRepo = Path.Combine(baseDir, "repo");
        var wtHome = Path.Combine(baseDir, "wt-home");
        var volRoot = Path.Combine(baseDir, "volumes");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(sourceRepo);
        Directory.CreateDirectory(wtHome);
        Directory.CreateDirectory(volRoot);

        RunGit(bareOrigin, ["init", "--bare", "--initial-branch=main"]);
        RunGit(sourceRepo, ["init", "--initial-branch=main"]);
        RunGit(sourceRepo, ["config", "user.name", "OS Owner"]);
        RunGit(sourceRepo, ["config", "user.email", "owner@intochat.test"]);

        var projDir = Path.Combine(sourceRepo, "modules", "MathKit");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(
            Path.Combine(sourceRepo, ".gitignore"),
            "bin/\nobj/\nartifacts/\n");
        File.WriteAllText(
            Path.Combine(projDir, "MathKit.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(projDir, "Calculator.cs"),
            """
            namespace MathKit;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        RunGit(sourceRepo, ["add", "."]);
        RunGit(sourceRepo, ["commit", "-m", "Initial compilable MathKit library"]);
        RunGit(sourceRepo, ["remote", "add", "origin", bareOrigin]);
        RunGit(sourceRepo, ["push", "-u", "origin", "main"]);

        return (bareOrigin, sourceRepo, wtHome, volRoot);
    }

    private sealed class ArchitectCriticModelInference(List<string> recordedPrompts) : IModelInference
    {
        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, Type? marker, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor(
                "Ollama",
                "qwen2.5-coder:14b",
                "default",
                "local",
                CapabilitySupport.Supported,
                CapabilitySupport.Unsupported,
                CapabilitySupport.Supported));

        public Task<InferenceResult> Generate(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId)
        {
            var combinedPrompt = string.Join("\n", request.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text));
            lock (recordedPrompts)
            {
                recordedPrompts.Add(combinedPrompt);
            }

            if (combinedPrompt.Contains("ARCHITECT", StringComparison.Ordinal))
            {
                const string architectJson = """
                    {
                      "goal": "Add Subtract and Clamp helpers to Calculator",
                      "architectureSummary": "Architect initial decomposition: add Subtract and Clamp on modules/MathKit/Calculator.cs.",
                      "tasks": [
                        {
                          "id": "t1-subtract",
                          "title": "Add Subtract helper via Roslyn AST",
                          "language": "csharp",
                          "targetFiles": ["modules/MathKit/Calculator.cs"],
                          "dependsOn": [],
                          "instructions": "Add public static int Subtract(int a, int b)"
                        }
                      ]
                    }
                    """;
                return Task.FromResult(new InferenceResult([new AiMessage("assistant", [new AiText(architectJson)])], "stop", null, null, "qwen2.5-coder:14b"));
            }

            const string criticJson = """
                {
                  "goal": "Add Subtract and Clamp helpers to Calculator",
                  "architectureSummary": "Partition Subtract and Clamp into sequential Roslyn AST edits across 2 isolated ContainerVolume worktrees.",
                  "criticReview": "Critic audited DDD invariants, file-count budget (1/5), Release TreatWarningsAsErrors compilation, and OTel/ITraceReader live probes.",
                  "transcript": [
                    { "speaker": "User", "role": "user", "perspective": "Goal & Scope", "message": "Add Subtract and Clamp helpers to Calculator" },
                    { "speaker": "Architect", "role": "architect", "perspective": "DDD & File Decomposition", "message": "Apply Roslyn AST edits to modules/MathKit/Calculator.cs across parallel worktrees." },
                    { "speaker": "Critic", "role": "critic", "perspective": "HouseRules & Edge-Case Critique", "message": "Enforce min/max ordering in Clamp, 0 compiler warnings, and SHA-bound owner approval." }
                  ],
                  "tasks": [
                    {
                      "id": "t1-subtract",
                      "title": "Add Subtract method via Roslyn AST",
                      "language": "csharp",
                      "targetFiles": ["modules/MathKit/Calculator.cs"],
                      "dependsOn": [],
                      "instructions": "Add Subtract(int a, int b)"
                    },
                    {
                      "id": "t2-clamp",
                      "title": "Add Clamp method via Roslyn AST",
                      "language": "csharp",
                      "targetFiles": ["modules/MathKit/Calculator.cs"],
                      "dependsOn": ["t1-subtract"],
                      "instructions": "Add Clamp(int value, int min, int max)"
                    }
                  ]
                }
                """;
            return Task.FromResult(new InferenceResult([new AiMessage("assistant", [new AiText(criticJson)])], "stop", null, null, "qwen2.5-coder:14b"));
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId)
            => throw new NotSupportedException();
    }

    private sealed class DelegatedRoslynCoder(IWorktreeRoslyn roslyn) : ICoder
    {
        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            var symbols = await roslyn.InspectSymbolsAsync(lease.WorkspacePath, "modules/MathKit", ct);
            Assert.Contains(symbols, static s => s.Name == "Calculator");

            var isSubtract = goal.Contains("t1-subtract", StringComparison.Ordinal);
            var memberName = isSubtract ? "Subtract" : "Clamp";
            var memberSource = isSubtract
                ? "public static int Subtract(int a, int b) => a - b;"
                : "public static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);";

            var edit = await roslyn.ApplyEditsAsync(
                lease.WorkspacePath,
                [
                    new WorktreeCSharpEdit(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "modules/MathKit/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: memberName,
                        Source: memberSource),
                ],
                ct);
            Assert.True(edit.Succeeded, edit.Detail);
            Assert.DoesNotContain(edit.Diagnostics, static d => d.Severity == "Error");

            return new CoderEditResult(
                $"Added {memberName} to MathKit.Calculator via WorktreeRoslynAnalyzer in lease {lease.EffectiveLeaseId}.",
                edit.TouchedFiles,
                "qwen2.5-coder:14b");
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
            }
        }
    }

    private static string RunGit(string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}\n{stdout}");
        }

        return stdout;
    }
}
