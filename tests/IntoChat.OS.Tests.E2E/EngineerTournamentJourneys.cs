using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.E2E;

/// <summary>
/// Live in-process OS end-to-end tournament journey proving true parallel candidate execution,
/// deterministic candidate scoring, side-by-side diff inspection &amp; human candidate promotion in <see cref="EngineerApp"/>,
/// SHA-bound confirmation re-minting at <c>approval-gate</c>, and bare Git remote publication of the promoted candidate.
/// </summary>
public sealed class EngineerTournamentJourneys
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ParallelTwoCandidateWorktreeTournamentScoresBothDiffsPromotesCandidateW2InEngineerAppAndPublishesW2CommitToOrigin()
    {
        using var tempHome = new TestHome();
        try
        {
            await RunTournamentJourneyAsync(tempHome.Path);
        }
        finally
        {
            ClearReadOnlyAttributes(tempHome.Path);
        }
    }

    private async Task RunTournamentJourneyAsync(string homeRoot)
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

        var observedPrompts = new List<(int InstanceIndex, string Prompt)>();
        var roslyn = new WorktreeRoslynAnalyzer();
        var coder = new TwoStrategyTournamentRoslynCoder(roslyn, observedPrompts);
        var planner = new TournamentPlanner();
        using var liveVerifier = new EngineerLiveVerifier(
            configuration,
            candidateVerifier: static (_, _) => Task.FromResult(new EngineerLiveReport(
                true, true, true, true, 0, 0, 0, 0,
                [new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> 200")],
                ["span candidate.health status=OK"],
                "Candidate verified.")));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IEngineerPlanner>(planner);
        serviceCollection.AddSingleton<ISandboxCommandRunner>(_ => ISandboxCommandRunner.CreateDefault(configuration: configuration));
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(sp =>
            new EngineerBuildVerifier(configuration, sp.GetRequiredService<ISandboxCommandRunner>()));
        serviceCollection.AddSingleton<ILiveVerifier>(liveVerifier);
        serviceCollection.AddSingleton<IWorktreeRoslyn>(roslyn);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        var brain = new BrainId($"tournament-{Guid.NewGuid():N}");
        using var owner = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.Runs.RunsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(EngineerApp).Assembly);

        // 1. Open app.engineer and launch an N = 2 parallel candidate tournament on ContainerVolume worktrees.
        var engineerScreen = os.Get<IScreen>(new NeuronId(brain, "app.engineer", "default"));
        Assert.NotNull(await engineerScreen.Read());

        await engineerScreen.Request(new EngineerNewRun(
            Repository: "os",
            Goal: "Add Subtract and bounded Clamp helpers to Calculator",
            InstanceCount: 2,
            Backend: "ContainerVolume"));
        await os.Idle();

        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var runs = os.Get<IEngineerRuns>(IEngineerRuns.Key(brain.Value));
        var activeFlows = await flows.List(new FlowQuery(Type: IEngineerRuns.EngineerFlow), Ct);
        var startedFlow = Assert.Single(activeFlows);
        var flowId = startedFlow.FlowId;

        // 2. Drive flow.engineer through brainstorm -> 2 parallel worktrees -> parallel strategy coding -> Release build -> live-verify -> approval-gate.
        EngineerRunSummary? waiting = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await runs.InspectEngineerRun(flowId, Ct);
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
            $"Expected tournament flow.engineer run to park at approval-gate, got {waiting.Status}: {waiting.SummaryText}");
        Assert.Equal("approval-gate", waiting.CurrentStage);
        Assert.Equal(2, waiting.ParallelInstances);
        Assert.NotNull(waiting.Candidates);
        Assert.Equal(2, waiting.Candidates!.Length);

        // Verify both candidate strategy lenses executed on isolated w1 and w2 leases.
        Assert.Contains(observedPrompts, static p => p.InstanceIndex == 0 && p.Prompt.Contains("surgical-patch", StringComparison.Ordinal));
        Assert.Contains(observedPrompts, static p => p.InstanceIndex == 1 && p.Prompt.Contains("clean-refactor", StringComparison.Ordinal));

        var candidateW1 = waiting.Candidates.Single(static c => c.InstanceIndex == 0);
        var candidateW2 = waiting.Candidates.Single(static c => c.InstanceIndex == 1);
        Assert.EndsWith("-w1", candidateW1.BranchName, StringComparison.Ordinal);
        Assert.EndsWith("-w2", candidateW2.BranchName, StringComparison.Ordinal);
        Assert.True(candidateW1.Score.AllGatesPassed, candidateW1.Score.Summary);
        Assert.True(candidateW2.Score.AllGatesPassed, candidateW2.Score.Summary);

        var shaW1 = candidateW1.Score.CommitSha!;
        var shaW2 = candidateW2.Score.CommitSha!;
        Assert.Equal(40, shaW1.Length);
        Assert.Equal(40, shaW2.Length);
        Assert.NotEqual(shaW1, shaW2);
        Assert.NotEqual(candidateW1.Score.UnifiedDiff, candidateW2.Score.UnifiedDiff);
        Assert.DoesNotContain("ClampNonNegative", candidateW1.Score.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("ClampNonNegative", candidateW2.Score.UnifiedDiff, StringComparison.Ordinal);

        // Candidate w1 has the smaller surgical diff -> higher deterministic score -> selected by default (#0)!
        Assert.True(candidateW1.Score.ChangedLines < candidateW2.Score.ChangedLines);
        Assert.True(candidateW1.Score.TotalScore > candidateW2.Score.TotalScore);
        Assert.Equal(1, candidateW1.Rank);
        Assert.Equal(2, candidateW2.Rank);
        Assert.Equal(0, waiting.SelectedCandidateIndex);
        Assert.Equal(shaW1, waiting.WinningCommitSha);

        var initialPlanId = waiting.ConfirmationPlanId!;
        Assert.EndsWith(":" + shaW1, initialPlanId, StringComparison.Ordinal);
        Assert.Equal(string.Empty, RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim());

        // 3. Inspect both candidates in EngineerApp, verify w1 is active initially, then promote Candidate w2 via UI!
        await engineerScreen.Request(new EngineerOpen(Tab: "pr", FlowId: flowId));
        await os.Idle();

        var engStepper = os.Get<IStepper>(new NeuronId(brain, "ui.stepper", EngineerApp.StepperKey));
        var engTree = os.Get<ITreeView>(new NeuronId(brain, "ui.tree-view", EngineerApp.WorktreeTreeKey));
        var engTabs = os.Get<ITabs>(new NeuronId(brain, "ui.tabs", EngineerApp.WorkspaceTabsKey));
        var engEditor = os.Get<ICodeEditor>(new NeuronId(brain, "ui.code-editor", EngineerApp.CodeEditorKey));
        var engTable = os.Get<ITable>(new NeuronId(brain, "ui.table", EngineerApp.TasksTableKey));

        Assert.Equal("review-and-pr", (await engStepper.Read()).ActiveStepId);
        Assert.Equal("pr", (await engTabs.Read()).SelectedId);
        Assert.Equal(2, (await engTree.Read()).Nodes.Count(static n => n.IsFolder));

        var editorBefore = (await engEditor.Read()).Content;
        Assert.Contains(shaW1, editorBefore, StringComparison.Ordinal);
        Assert.DoesNotContain("ClampNonNegative", editorBefore, StringComparison.Ordinal);

        var scoreboardBefore = await engTable.Read();
        Assert.Contains(scoreboardBefore.Rows, static r => r[0].StartsWith("w1", StringComparison.Ordinal) && r[4].Contains("SELECTED", StringComparison.Ordinal));
        Assert.Contains(scoreboardBefore.Rows, static r => r[0].StartsWith("w2", StringComparison.Ordinal) && r[4].Contains("STANDBY", StringComparison.Ordinal));

        // Promote Candidate w2 by clicking 'Select Candidate w2' ('engineer:candidate:select:2') on EngineerApp!
        var liveView = (await engineerScreen.Read())!;
        await engineerScreen.Activate(liveView.Alias, liveView.Generation, EngineerApp.CandidateSelectKey(1));
        await os.Idle();

        // Verify EngineerApp switched ICodeEditor to w2's diff and updated ITable scoreboard to mark w2 SELECTED!
        var editorAfter = (await engEditor.Read()).Content;
        Assert.Contains(shaW2, editorAfter, StringComparison.Ordinal);
        Assert.Contains("ClampNonNegative", editorAfter, StringComparison.Ordinal);

        var scoreboardAfter = await engTable.Read();
        Assert.Contains(scoreboardAfter.Rows, static r => r[0].StartsWith("w2", StringComparison.Ordinal) && r[4].Contains("SELECTED", StringComparison.Ordinal));
        Assert.Contains(scoreboardAfter.Rows, static r => r[0].StartsWith("w1", StringComparison.Ordinal) && r[4].Contains("STANDBY", StringComparison.Ordinal));

        // Verify flow.engineer re-bound its SHA-locked confirmation plan from shaW1 -> shaW2 and withdrew initialPlanId!
        var afterPromotion = await runs.InspectEngineerRun(flowId, Ct);
        Assert.Equal(1, afterPromotion.SelectedCandidateIndex);
        Assert.Equal(shaW2, afterPromotion.WinningCommitSha);
        var promotedPlanId = afterPromotion.ConfirmationPlanId!;
        Assert.NotEqual(initialPlanId, promotedPlanId);
        Assert.EndsWith(":" + shaW2, promotedPlanId, StringComparison.Ordinal);

        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var pendingAfterSwitch = (await inbox.Read()).Pending;
        Assert.DoesNotContain(pendingAfterSwitch, p => p.PlanId == initialPlanId);
        var pendingW2 = Assert.Single(pendingAfterSwitch, p => p.PlanId == promotedPlanId);
        Assert.Equal(EngineerFlow.ConfirmationKind, pendingW2.Plan.Kind);
        Assert.Equal(shaW2, pendingW2.Plan.Subject);

        // 4. Approve the new SHA-bound confirmation gate as Person ('owner'), push to bare origin, and verify w2 landed & both worktrees cleaned!
        await inbox.Resolve(promotedPlanId, approve: true);

        EngineerRunSummary? completed = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await runs.InspectEngineerRun(flowId, Ct);
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
            $"Expected flow.engineer to complete after owner approval of promoted w2 plan, got {completed.Status}: {completed.SummaryText}");
        Assert.Equal(EngineerFlow.Names, completed.CompletedStages);
        Assert.Equal(1, completed.SelectedCandidateIndex);
        Assert.Equal(shaW2, completed.WinningCommitSha);
        Assert.Equal($"refs/heads/{candidateW2.BranchName}", completed.PushedRef);

        // Verify ONLY Candidate w2's branch & exact commit SHA + refactored code landed on bare origin!
        var remoteBranches = RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim();
        Assert.Contains(candidateW2.BranchName, remoteBranches, StringComparison.Ordinal);
        Assert.DoesNotContain(candidateW1.BranchName, remoteBranches, StringComparison.Ordinal);

        var remoteCommitSha = RunGit(bareOrigin, ["rev-parse", completed.PushedRef!]).Trim();
        Assert.Equal(shaW2, remoteCommitSha);

        var remoteCalculatorSource = RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Calculator.cs"]);
        Assert.Contains("public static int Subtract(int a, int b) => checked(a - b);", remoteCalculatorSource, StringComparison.Ordinal);
        Assert.Contains("public static int ClampNonNegative(int value, int max)", remoteCalculatorSource, StringComparison.Ordinal);

        // Verify both parallel ContainerVolume worktrees were cleaned up on disk.
        var starter = os.Get<IFlowStarter>(NeuronId.Parse(flowId));
        using (var finalDoc = JsonDocument.Parse((await starter.Head()).InnerJson))
        {
            foreach (var inst in finalDoc.RootElement.GetProperty("instances").EnumerateArray())
            {
                var treePath = inst.GetProperty("lease").GetProperty("treePath").GetString()!;
                Assert.False(Directory.Exists(treePath), $"Worktree '{treePath}' must be cleaned up after tournament publication.");
            }
        }

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

    private sealed class TournamentPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Execute parallel candidate strategies on MathKit.Calculator across isolated ContainerVolume worktrees.",
                CriticReview: "Score candidates deterministically on Release build, zero warnings, zero telemetry errors, and diff cleanliness.",
                Transcript:
                [
                    new("User", "user", "Goal & Scope", goal),
                    new("Architect", "architect", "Parallel Tournament", "Run w1 surgical patch and w2 alternative refactor concurrently."),
                    new("Critic", "critic", "SHA-Bound Gate", "Rank candidates deterministically and re-mint ConfirmationPlan if human switches winner."),
                ],
                Tasks:
                [
                    new("t1-subtract", "Add Subtract helper via Roslyn AST", "csharp", ["modules/MathKit/Calculator.cs"], [], "Add Subtract"),
                    new("t2-clamp", "Add bounded Clamp helper(s) via Roslyn AST", "csharp", ["modules/MathKit/Calculator.cs"], ["t1-subtract"], "Add Clamp"),
                ]));
    }

    private sealed class TwoStrategyTournamentRoslynCoder(
        IWorktreeRoslyn roslyn,
        List<(int InstanceIndex, string Prompt)> observedPrompts) : ICoder
    {
        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            lock (observedPrompts)
            {
                observedPrompts.Add((lease.InstanceIndex, goal));
            }

            var isSubtract = goal.Contains("t1-subtract", StringComparison.Ordinal);
            WorktreeCSharpEdit[] edits;

            if (lease.InstanceIndex == 0)
            {
                // Candidate w1 (surgical-patch): concise minimal patch (smaller diff -> higher default score)
                edits = isSubtract
                    ? [
                        new(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "Subtract",
                            Source: "public static int Subtract(int a, int b) => a - b;"),
                    ]
                    : [
                        new(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "Clamp",
                            Source: "public static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);"),
                    ];
            }
            else
            {
                // Candidate w2 (clean-refactor): checked arithmetic + explicit guard + extra ClampNonNegative helper
                edits = isSubtract
                    ? [
                        new(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "Subtract",
                            Source: "public static int Subtract(int a, int b) => checked(a - b);"),
                    ]
                    : [
                        new(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "Clamp",
                            Source: """
                                public static int Clamp(int value, int min, int max)
                                {
                                    if (min > max)
                                    {
                                        throw new global::System.ArgumentOutOfRangeException(nameof(min), "min cannot exceed max.");
                                    }

                                    return value < min ? min : (value > max ? max : value);
                                }
                                """),
                        new(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "ClampNonNegative",
                            Source: "public static int ClampNonNegative(int value, int max) => Clamp(value, 0, max < 0 ? 0 : max);"),
                    ];
            }

            var result = await roslyn.ApplyEditsAsync(lease.WorkspacePath, edits, ct);
            Assert.True(result.Succeeded, result.Detail);
            Assert.DoesNotContain(result.Diagnostics, static d => d.Severity == "Error");

            return new CoderEditResult(
                $"Candidate w{lease.InstanceIndex + 1} applied {edits.Length} Roslyn AST edit(s) on MathKit.Calculator.",
                result.TouchedFiles,
                $"roslyn-tournament-w{lease.InstanceIndex + 1}");
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
