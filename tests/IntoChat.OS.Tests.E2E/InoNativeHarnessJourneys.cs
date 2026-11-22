using System.Diagnostics;
using System.Text;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using IntoChat.OS.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.E2E;

/// <summary>
/// Live in-process OS end-to-end journey proving <c>InoNativeHarness</c> (#1 primary engineering engine):
/// pre-flight <see cref="IWorkingMemory"/> brief hydration (&lt; 3 KB), plan ratification (<see cref="IFlows.RatifyEngineerPlan"/>),
/// <c>CleanupTargets</c> dead-file deletion, 2-level parallelism (<c>2</c> isolated worktrees × <c>2</c> parallel intra-worktree
/// Roslyn AST slices via <c>Task.WhenAll</c>), real <c>dotnet build -c Release</c>, <see cref="ISessionJournal"/> writeback,
/// and human SHA-bound bare Git remote publication.
/// </summary>
public sealed class InoNativeHarnessJourneys(TestOSFixture fixture) : E2ETestBase, IClassFixture<TestOSFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InoNativeMissionHydratesWorkingMemoryDeletesCleanupTargetsRunsTwoByTwoParallelRoslynSlicesWritesSessionJournalAndPushesToOrigin()
    {
        using var tempHome = new TestHome();
        try
        {
            await RunInoNativeMissionJourneyAsync(tempHome.Path);
        }
        finally
        {
            ClearReadOnlyAttributes(tempHome.Path);
        }
    }

    [Fact]
    public async Task InoNativeMissionSynthesizesCodeGraphPlanAndSelfRepairsSliceDiagnosticsEndToEnd()
    {
        using var tempHome = new TestHome();
        try
        {
            await RunCodeGraphAndSelfRepairJourneyAsync(tempHome.Path);
        }
        finally
        {
            ClearReadOnlyAttributes(tempHome.Path);
        }
    }

    private async Task RunInoNativeMissionJourneyAsync(string homeRoot)
    {
        var (bareOrigin, sourceRepo, workTreeHome, volumeRoot) = SeedCompilableGitRepositoryWithDeadFile(homeRoot);

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

        var observedSlicePrompts = new List<(int InstanceIndex, string SliceId, string Prompt)>();
        var activeSlicesByWorktree = new Dictionary<int, int>();
        var peakSlicesByWorktree = new Dictionary<int, int>();
        var gate = new Lock();

        var roslyn = new WorktreeRoslynAnalyzer();
        var coder = new TwoByTwoParallelRoslynSliceCoder(
            roslyn,
            observedSlicePrompts,
            onEnter: wtIdx =>
            {
                lock (gate)
                {
                    var next = activeSlicesByWorktree.GetValueOrDefault(wtIdx) + 1;
                    activeSlicesByWorktree[wtIdx] = next;
                    if (next > peakSlicesByWorktree.GetValueOrDefault(wtIdx))
                    {
                        peakSlicesByWorktree[wtIdx] = next;
                    }
                }
            },
            onExit: wtIdx =>
            {
                lock (gate)
                {
                    activeSlicesByWorktree[wtIdx] = Math.Max(0, activeSlicesByWorktree.GetValueOrDefault(wtIdx) - 1);
                }
            });

        var planner = new DisjointTwoSlicePlanner();
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
        serviceCollection.AddSingleton(roslyn);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = fixture.CreateIsolated(o => o.Services = services);
        var brain = NewBrain("inonative");
        using var owner = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        os.Register(typeof(AIModule).Assembly);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(EngineerApp).Assembly);

        // 1. Seed IWorkingMemory with SOUL, USER, and a lazy-loaded PROJECT brief for 'os'.
        var workingMemory = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(brain));
        await workingMemory.SetSlice(WorkingMemorySliceKind.Soul, "Ino native parallel engineering engine.");
        await workingMemory.SetSlice(WorkingMemorySliceKind.User, "Owner requires zero warnings and clean trash deletion.");
        await workingMemory.PutBrief(
            "os-mathkit-brief",
            "os MathKit Project Architecture Brief",
            "project",
            "Delete LegacyObsoleteMath.cs before adding Subtract on Calculator and Dot on Vector2Int.");

        // 2. Start a 2-worktree InoNative mission with RequirePlanRatification = true.
        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var started = await flows.StartEngineerRun(
            new EngineerDelegationRequest(
                RepoSlug: "os",
                Goal: "Delete LegacyObsoleteMath and implement Subtract on Calculator plus Dot on Vector2Int",
                ParallelInstances: 2,
                Backend: "ContainerVolume",
                RunId: "inonative2x2",
                RequirePlanRatification: true,
                Harness: EngineeringHarnessKind.InoNative,
                CleanupTargets: ["modules/MathKit/LegacyObsoleteMath.cs"]),
            Ct);

        var flowId = started.FlowId;
        EngineerRunSummary? awaitingRatification = null;
        for (var poll = 0; poll < 80; poll++)
        {
            await os.Idle();
            var snap = await flows.InspectEngineerRun(flowId, Ct);
            if (snap.AwaitingPlanRatification)
            {
                awaitingRatification = snap;
                break;
            }

            await Task.Delay(30, Ct);
        }

        Assert.NotNull(awaitingRatification);
        Assert.Equal(FlowStatus.Waiting, awaitingRatification!.Status);
        Assert.Equal("brainstorm-and-plan", awaitingRatification.CurrentStage);
        Assert.Equal(EngineeringHarnessKind.InoNative, awaitingRatification.Harness);
        Assert.NotNull(awaitingRatification.Collaborators);
        Assert.Contains(awaitingRatification.Collaborators!, static c => c.Kind == "person");
        Assert.Equal(2, awaitingRatification.Collaborators!.Count(static c => c.Kind == "ino-agent"));

        // 3. Ratify the plan with 2 disjoint parallel Wave-0 slices + LegacyObsoleteMath.cs cleanup target!
        EngineerTaskSlice[] approvedSlices =
        [
            new(
                SliceId: "slice-calc",
                Title: "Add Subtract to Calculator",
                OwnedPaths: ["modules/MathKit/Calculator.cs"],
                DependsOnSliceIds: [],
                AssignedAgentKey: "ino.slice-calc",
                Language: "csharp",
                Instructions: "Add Subtract to Calculator"),
            new(
                SliceId: "slice-vec",
                Title: "Add Dot product to Vector2Int",
                OwnedPaths: ["modules/MathKit/Vector2Int.cs"],
                DependsOnSliceIds: [],
                AssignedAgentKey: "ino.slice-vec",
                Language: "csharp",
                Instructions: "Add Dot to Vector2Int"),
        ];

        _ = await flows.RatifyEngineerPlan(
            flowId,
            approvedCleanupTargets: ["modules/MathKit/LegacyObsoleteMath.cs"],
            slices: approvedSlices,
            ct: Ct);

        // 4. Drive through 2 worktrees × 2 parallel slices -> Release dotnet build -> live-verify -> approval-gate!
        EngineerRunSummary? waitingAtGate = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snap = await flows.InspectEngineerRun(flowId, Ct);
            if (snap.Status == FlowStatus.Waiting && snap.CurrentStage == "approval-gate")
            {
                waitingAtGate = snap;
                break;
            }

            if (snap.Status is FlowStatus.Failed or FlowStatus.Completed)
            {
                waitingAtGate = snap;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(waitingAtGate);
        Assert.True(
            waitingAtGate!.Status == FlowStatus.Waiting && waitingAtGate.CurrentStage == "approval-gate",
            $"Expected mission to park at approval-gate, got {waitingAtGate.Status} ({waitingAtGate.CurrentStage}): {waitingAtGate.SummaryText}");

        // Verify 2 worktrees × 2 parallel intra-worktree slices = 4 slice executions, all hydrated with IWorkingMemory brief!
        Assert.Equal(4, observedSlicePrompts.Count);
        Assert.All(observedSlicePrompts, static p =>
            Assert.Contains("BRIEF[os-mathkit-brief]: Delete LegacyObsoleteMath.cs", p.Prompt, StringComparison.Ordinal));
        Assert.Equal(2, peakSlicesByWorktree.GetValueOrDefault(0));
        Assert.Equal(2, peakSlicesByWorktree.GetValueOrDefault(1));

        Assert.All(waitingAtGate.Instances, static inst =>
        {
            Assert.True(inst.BuildSucceeded);
            Assert.True(inst.LiveSucceeded);
            Assert.Equal(["modules/MathKit/LegacyObsoleteMath.cs"], inst.DeletedFiles!);
            Assert.Contains("modules/MathKit/LegacyObsoleteMath.cs", inst.ChangedFiles);
            Assert.Contains("modules/MathKit/Calculator.cs", inst.ChangedFiles);
            Assert.Contains("modules/MathKit/Vector2Int.cs", inst.ChangedFiles);
        });

        // Verify ISessionJournal recorded the approval-gate milestone with project brief & open approval thread!
        var sessionJournal = os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(brain));
        var journalAtGate = await sessionJournal.ReadReturningView();
        Assert.Equal("os-mathkit-brief", journalAtGate.ActiveProjectBrief);
        Assert.NotEmpty(journalAtGate.Sessions);
        Assert.Contains(journalAtGate.OpenThreads, t => t.Contains(waitingAtGate.ConfirmationPlanId!, StringComparison.Ordinal));

        // 5. Approve SHA-bound confirmation gate as Person ('owner') and verify push to bare Git remote + cleanup + journal resolution!
        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        await inbox.Resolve(waitingAtGate.ConfirmationPlanId!, approve: true);

        EngineerRunSummary? completed = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snap = await flows.InspectEngineerRun(flowId, Ct);
            if (snap.Status is FlowStatus.Completed or FlowStatus.Failed)
            {
                completed = snap;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(completed);
        Assert.True(
            completed!.Status == FlowStatus.Completed,
            $"Expected InoNative mission to complete cleanly, got {completed.Status}: {completed.SummaryText}");
        Assert.Equal(EngineerFlow.Names, completed.CompletedStages);
        Assert.NotNull(completed.PushedRef);

        var remoteCommitSha = RunGit(bareOrigin, ["rev-parse", completed.PushedRef!]).Trim();
        Assert.Equal(completed.WinningCommitSha, remoteCommitSha);

        // Verify LegacyObsoleteMath.cs was deleted in the pushed commit while Calculator.cs & Vector2Int.cs gained their new methods!
        var remoteTreeFiles = RunGit(bareOrigin, ["ls-tree", "-r", "--name-only", remoteCommitSha]);
        Assert.DoesNotContain("LegacyObsoleteMath.cs", remoteTreeFiles, StringComparison.Ordinal);
        Assert.Contains("modules/MathKit/Calculator.cs", remoteTreeFiles, StringComparison.Ordinal);
        Assert.Contains("modules/MathKit/Vector2Int.cs", remoteTreeFiles, StringComparison.Ordinal);

        var remoteCalc = RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Calculator.cs"]);
        var remoteVec = RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Vector2Int.cs"]);
        Assert.Contains("Subtract(", remoteCalc, StringComparison.Ordinal);
        Assert.Contains("Dot(", remoteVec, StringComparison.Ordinal);

        var journalAfter = await sessionJournal.ReadReturningView();
        Assert.DoesNotContain(journalAfter.OpenThreads, t => t.Contains(waitingAtGate.ConfirmationPlanId!, StringComparison.Ordinal));
        Assert.Contains(completed.PushedRef!, journalAfter.FormattedMarkdown, StringComparison.Ordinal);
    }

    private async Task RunCodeGraphAndSelfRepairJourneyAsync(string homeRoot)
    {
        var (bareOrigin, sourceRepo, workTreeHome, volumeRoot) = SeedCompilableGitRepositoryWithDeadFile(homeRoot);

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
                Backend = WorkTreeBackendKind.LocalGit,
                VerifyFormatting = false,
            });
        workTree.RegisterRepository(new RegisteredRepository(
            Id: "os",
            LocalPath: sourceRepo,
            DefaultBaseBranch: "main",
            RemoteCloneUrl: bareOrigin));

        var roslyn = new WorktreeRoslynAnalyzer();
        var observedRepairPrompts = new List<string>();
        var coder = new SelfRepairingCodeGraphSliceCoder(roslyn, observedRepairPrompts);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IEngineerPlanner>(sp => new EngineerPlanner(sp));
        serviceCollection.AddSingleton<ISandboxCommandRunner>(_ => ISandboxCommandRunner.CreateDefault(configuration: configuration));
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(sp =>
            new EngineerBuildVerifier(configuration, sp.GetRequiredService<ISandboxCommandRunner>()));
        serviceCollection.AddSingleton<ILiveVerifier>(sp =>
            new EngineerLiveVerifier(
                configuration: configuration,
                services: sp,
                candidateVerifier: static (_, _) => Task.FromResult(new EngineerLiveReport(
                    true, true, true, true, 0, 0, 0, 0,
                    [new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> 200")],
                    ["span candidate.health status=OK"],
                    "Candidate verified."))));
        serviceCollection.AddSingleton(roslyn);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = fixture.CreateIsolated(o => o.Services = services);
        var brain = NewBrain("inonativev2");
        using var owner = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        os.Register(typeof(AIModule).Assembly);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(EngineerApp).Assembly);

        // Seed repo:os brief in IWorkingMemory so EngineerPlanner hydrates both CodeGraph + WorkingMemory before planning!
        var workingMemory = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(brain));
        await workingMemory.PutBrief(
            "repo:os",
            "os MathKit Repository Brief",
            "project",
            "Delete LegacyObsoleteMath.cs and keep Calculator and Vector2Int pure value methods.");

        // Start mission WITHOUT hardcoded Slices or CleanupTargets: EngineerPlanner synthesizes both from live CodeGraph + repo:os brief!
        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var started = await flows.StartEngineerRun(
            new EngineerDelegationRequest(
                RepoSlug: "os",
                Goal: "Delete LegacyObsoleteMath and implement Subtract on Calculator plus Dot on Vector2Int",
                ParallelInstances: 1,
                Backend: "LocalGit",
                RunId: "inonativev2run",
                RequirePlanRatification: false,
                Harness: EngineeringHarnessKind.InoNative),
            Ct);

        var flowId = started.FlowId;
        EngineerRunSummary? waitingAtGate = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snap = await flows.InspectEngineerRun(flowId, Ct);
            if (snap.Status == FlowStatus.Waiting && snap.CurrentStage == "approval-gate")
            {
                waitingAtGate = snap;
                break;
            }

            if (snap.Status is FlowStatus.Failed or FlowStatus.Completed)
            {
                waitingAtGate = snap;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(waitingAtGate);
        Assert.True(
            waitingAtGate!.Status == FlowStatus.Waiting && waitingAtGate.CurrentStage == "approval-gate",
            $"Expected CodeGraph + self-repair mission to reach approval-gate, got {waitingAtGate.Status} ({waitingAtGate.CurrentStage}): {waitingAtGate.SummaryText}");

        // 1. Assert CodeGraph-hydrated planning: <= 2 KB digest, grounded LegacyObsoleteMath cleanup, and disjoint parallel slices!
        Assert.NotNull(waitingAtGate.RatifiedPlan);
        Assert.False(string.IsNullOrWhiteSpace(waitingAtGate.RatifiedPlan!.CodeGraphDigest));
        Assert.True(Encoding.UTF8.GetByteCount(waitingAtGate.RatifiedPlan.CodeGraphDigest!) <= RepositoryCodeGraphDigest.MaxBytes);
        Assert.Contains("BRIEF[repo:os]:", waitingAtGate.RatifiedPlan.CodeGraphDigest!, StringComparison.Ordinal);
        Assert.Contains("modules/MathKit/LegacyObsoleteMath.cs", waitingAtGate.CleanupTargets!);
        Assert.False(waitingAtGate.RatifiedPlan.DisjointSlicesRule().Broken);
        Assert.Contains(waitingAtGate.DiagnosticsAndTraces, static t => t.StartsWith("plan.codegraph", StringComparison.Ordinal));

        // 2. Assert bounded intra-slice Roslyn self-repair telemetry end-to-end (Turn 1 CS0029 -> Turn 2 repair, SelfRepairAttempts == 1)!
        Assert.NotEmpty(observedRepairPrompts);
        Assert.Contains(observedRepairPrompts, static p => p.Contains("CS0029", StringComparison.Ordinal));
        Assert.NotNull(waitingAtGate.Slices);
        var repairedSlice = Assert.Single(waitingAtGate.Slices!, static s => s.SelfRepairAttempts > 0);
        Assert.Equal(1, repairedSlice.SelfRepairAttempts);
        Assert.Contains(waitingAtGate.DiagnosticsAndTraces, static t => t.StartsWith("ino.self-repair", StringComparison.Ordinal));

        // Verify live brain journal recorded the OperationProgressed(Phase: "self-repair") signal!
        var publishedProgress = os.Journal(brain)
            .Signals(NeuronId.Parse(flowId))
            .Select(static j => j.Signal)
            .OfType<OperationProgressed>()
            .ToArray();
        Assert.Contains(
            publishedProgress,
            static p => p.Phase == "self-repair"
                && p.Detail.Contains("Repairing CS0029 in modules/MathKit/Calculator.cs (attempt 1/2)", StringComparison.Ordinal));

        // 3. Approve as Person and verify clean remote git publication!
        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        await inbox.Resolve(waitingAtGate.ConfirmationPlanId!, approve: true);

        EngineerRunSummary? completed = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snap = await flows.InspectEngineerRun(flowId, Ct);
            if (snap.Status is FlowStatus.Completed or FlowStatus.Failed)
            {
                completed = snap;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(completed);
        Assert.Equal(FlowStatus.Completed, completed!.Status);
        var remoteCommitSha = RunGit(bareOrigin, ["rev-parse", completed.PushedRef!]).Trim();
        Assert.Equal(completed.WinningCommitSha, remoteCommitSha);
        var remoteFiles = RunGit(bareOrigin, ["ls-tree", "-r", "--name-only", remoteCommitSha]);
        Assert.DoesNotContain("LegacyObsoleteMath.cs", remoteFiles, StringComparison.Ordinal);
        Assert.Contains("Subtract(", RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Calculator.cs"]), StringComparison.Ordinal);
        Assert.Contains("Dot(", RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Vector2Int.cs"]), StringComparison.Ordinal);
    }

    private static (string BareOrigin, string SourceRepo, string WorkTreeHome, string VolumeRoot) SeedCompilableGitRepositoryWithDeadFile(string baseDir)
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
        File.WriteAllText(Path.Combine(sourceRepo, ".gitignore"), "bin/\nobj/\nartifacts/\n");
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
        File.WriteAllText(
            Path.Combine(projDir, "Vector2Int.cs"),
            """
            namespace MathKit;

            public readonly record struct Vector2Int(int X, int Y);
            """);
        File.WriteAllText(
            Path.Combine(projDir, "LegacyObsoleteMath.cs"),
            """
            namespace MathKit;

            public static class LegacyObsoleteMath
            {
                public static int Obsolete() => -1;
            }
            """);

        RunGit(sourceRepo, ["add", "."]);
        RunGit(sourceRepo, ["commit", "-m", "Initial MathKit with Calculator, Vector2Int, and LegacyObsoleteMath"]);
        RunGit(sourceRepo, ["remote", "add", "origin", bareOrigin]);
        RunGit(sourceRepo, ["push", "-u", "origin", "main"]);

        return (bareOrigin, sourceRepo, wtHome, volRoot);
    }

    private sealed class DisjointTwoSlicePlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Delete LegacyObsoleteMath.cs and execute 2 parallel Wave-0 Roslyn slices on Calculator.cs and Vector2Int.cs.",
                CriticReview: "Verified disjoint OwnedPaths across parallel Wave-0 slices and zero compiler warnings.",
                Transcript:
                [
                    new("User", "user", "Goal", goal),
                    new("Architect", "architect", "2x2 Slice Plan", "Run slice-calc and slice-vec concurrently in Wave 0."),
                    new("Critic", "critic", "Disjoint Rule", "NoOverlappingFileSlices holds."),
                ],
                Tasks:
                [
                    new("slice-calc", "Add Subtract to Calculator", "csharp", ["modules/MathKit/Calculator.cs"], [], "Add Subtract"),
                    new("slice-vec", "Add Dot to Vector2Int", "csharp", ["modules/MathKit/Vector2Int.cs"], [], "Add Dot"),
                ],
                CleanupTargets: ["modules/MathKit/LegacyObsoleteMath.cs"],
                Slices:
                [
                    new("slice-calc", "Add Subtract to Calculator", ["modules/MathKit/Calculator.cs"], [], AssignedAgentKey: "ino.slice-calc"),
                    new("slice-vec", "Add Dot to Vector2Int", ["modules/MathKit/Vector2Int.cs"], [], AssignedAgentKey: "ino.slice-vec"),
                ]));
    }

    private sealed class TwoByTwoParallelRoslynSliceCoder(
        IWorktreeRoslyn roslyn,
        List<(int InstanceIndex, string SliceId, string Prompt)> observedPrompts,
        Action<int> onEnter,
        Action<int> onExit) : ICoder
    {
        private readonly ConcurrentBarrierPair _barriers = new();

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            var isCalc = goal.Contains("slice-calc", StringComparison.Ordinal);
            var sliceId = isCalc ? "slice-calc" : "slice-vec";

            lock (observedPrompts)
            {
                observedPrompts.Add((lease.InstanceIndex, sliceId, goal));
            }

            onEnter(lease.InstanceIndex);
            try
            {
                // Synchronize both Wave-0 slices inside the same worktree to prove true Level-2 Task.WhenAll concurrency!
                await _barriers.ArriveAndWaitAsync(lease.InstanceIndex, expected: 2, ct);

                WorktreeCSharpEdit edit = isCalc
                    ? new(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "modules/MathKit/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: "Subtract",
                        Source: lease.InstanceIndex == 0
                            ? "public static int Subtract(int a, int b) => a - b;"
                            : "public static int Subtract(int a, int b) => checked(a - b);")
                    : new(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "modules/MathKit/Vector2Int.cs",
                        TypeName: "Vector2Int",
                        MemberName: "Dot",
                        Source: "public static int Dot(Vector2Int a, Vector2Int b) => (a.X * b.X) + (a.Y * b.Y);");

                var result = await roslyn.ApplyEditsAsync(lease.WorkspacePath, [edit], ct);
                Assert.True(result.Succeeded, result.Detail);
                return new CoderEditResult(
                    $"Completed {sliceId} on w{lease.InstanceIndex + 1}.",
                    result.TouchedFiles,
                    $"ino-native-w{lease.InstanceIndex + 1}");
            }
            finally
            {
                onExit(lease.InstanceIndex);
            }
        }
    }

    private sealed class SelfRepairingCodeGraphSliceCoder(
        IWorktreeRoslyn roslyn,
        List<string> observedRepairPrompts) : ICoder
    {
        private int _calcAttempts;

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            var isCalc = goal.Contains("Calculator.cs", StringComparison.Ordinal);
            if (isCalc)
            {
                var attempt = Interlocked.Increment(ref _calcAttempts);
                if (attempt == 1)
                {
                    // Inject a real C# type error (CS0029) on Turn 1 so InoNativeHarness triggers bounded Roslyn self-repair!
                    var calcPath = Path.Combine(lease.WorkspacePath, "modules", "MathKit", "Calculator.cs");
                    await File.WriteAllTextAsync(
                        calcPath,
                        """
                        namespace MathKit;

                        public static class Calculator
                        {
                            public static int Add(int a, int b) => a + b;
                            public static int Subtract(int a, int b) => "broken_return_type";
                        }
                        """,
                        ct);
                    return new CoderEditResult("Drafted Calculator.Subtract (turn 1).", ["modules/MathKit/Calculator.cs"], "ino-native-e2e");
                }

                lock (observedRepairPrompts)
                {
                    observedRepairPrompts.Add(goal);
                }

                // First restore syntactically clean Calculator.cs or replace member cleanly via AST
                var repaired = await roslyn.ApplyEditsAsync(
                    lease.WorkspacePath,
                    [
                        new WorktreeCSharpEdit(
                            WorktreeCSharpEditKind.AddOrReplaceMember,
                            "modules/MathKit/Calculator.cs",
                            TypeName: "Calculator",
                            MemberName: "Subtract",
                            Source: "public static int Subtract(int a, int b) => a - b;"),
                    ],
                    ct);
                Assert.True(repaired.Succeeded, repaired.Detail);
                return new CoderEditResult("Repaired CS0029 on Calculator.Subtract.", repaired.TouchedFiles, "ino-native-e2e");
            }

            var vecEdit = await roslyn.ApplyEditsAsync(
                lease.WorkspacePath,
                [
                    new WorktreeCSharpEdit(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "modules/MathKit/Vector2Int.cs",
                        TypeName: "Vector2Int",
                        MemberName: "Dot",
                        Source: "public static int Dot(Vector2Int a, Vector2Int b) => (a.X * b.X) + (a.Y * b.Y);"),
                ],
                ct);
            Assert.True(vecEdit.Succeeded, vecEdit.Detail);
            return new CoderEditResult("Added Vector2Int.Dot.", vecEdit.TouchedFiles, "ino-native-e2e");
        }
    }

    private sealed class ConcurrentBarrierPair
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, (int Count, TaskCompletionSource Ready)> _perWorktree = [];

        public Task ArriveAndWaitAsync(int instanceIndex, int expected, CancellationToken ct)
        {
            Task waitTask;
            lock (_gate)
            {
                if (!_perWorktree.TryGetValue(instanceIndex, out var entry))
                {
                    entry = (0, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                var nextCount = entry.Count + 1;
                entry = (nextCount, entry.Ready);
                _perWorktree[instanceIndex] = entry;
                if (nextCount >= expected)
                {
                    entry.Ready.TrySetResult();
                }

                waitTask = entry.Ready.Task;
            }

            return waitTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
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
