using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.E2E;

/// <summary>
/// Live in-process OS end-to-end dogfood journey exercising the unified 3-app loop:
/// <see cref="EngineerApp"/> (<c>app.engineer</c>), <see cref="EngineerFlow"/> (<c>flow.engineer</c>)
/// across <c>N = 2</c> isolated container-volume git worktrees with real Roslyn AST edits and sandbox build/live checks,
/// <see cref="CreatorApp"/> (<c>app.creator</c>) causal signal time-travel (<see cref="CreatorReplay"/> + <see cref="IPropertyGrid"/>),
/// and owner SHA-bound confirmation pushing to a bare Git remote and cleaning both worktrees.
/// </summary>
public sealed class EngineerDogfoodJourneys
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnifiedEngineerFlowAndCreatorTimeTravelDogfoodLoopPublishesTwoWorktreeRoslynChangeToBareRemote()
    {
        using var tempHome = new TestHome();
        try
        {
            await RunDogfoodLoopAsync(tempHome.Path);
        }
        finally
        {
            ClearReadOnlyAttributes(tempHome.Path);
        }
    }

    private async Task RunDogfoodLoopAsync(string homeRoot)
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

        var sandboxInvocations = new List<SandboxCommandRequest>();
        var realSandboxRunner = ISandboxCommandRunner.CreateDefault(configuration: configuration);
        var auditedSandboxRunner = new AuditingSandboxCommandRunner(realSandboxRunner, sandboxInvocations);

        var roslyn = new WorktreeRoslynAnalyzer();
        var coder = new DogfoodRoslynCoder(roslyn);
        var planner = new DogfoodTwoTaskPlanner();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IEngineerPlanner>(planner);
        serviceCollection.AddSingleton<ISandboxCommandRunner>(auditedSandboxRunner);
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(sp =>
            new EngineerBuildVerifier(configuration, sp.GetRequiredService<ISandboxCommandRunner>()));
        serviceCollection.AddSingleton<ILiveVerifier>(_ => new EngineerLiveVerifier(
            configuration,
            candidateVerifier: static (_, _) => Task.FromResult(new EngineerLiveReport(
                true, true, true, true, 0, 0, 0, 0,
                [new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> 200")],
                ["span candidate.health status=OK"],
                "Candidate verified."))));
        serviceCollection.AddSingleton<IWorktreeRoslyn>(roslyn);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        var brain = new BrainId($"dogfood-{Guid.NewGuid():N}");
        using var owner = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(EngineerApp).Assembly);

        // 1. Open app.engineer and launch a real 2-worktree ContainerVolume flow.engineer run.
        var engineerId = new NeuronId(brain, "app.engineer", "default");
        var engineerScreen = os.Get<IScreen>(engineerId);
        Assert.NotNull(await engineerScreen.Read());

        await engineerScreen.Request(new EngineerNewRun(
            Repository: "os",
            Goal: "Add Subtract and Clamp helpers to Calculator",
            InstanceCount: 2,
            Backend: "ContainerVolume"));
        await os.Idle();

        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var activeFlows = await flows.List(new FlowQuery(Type: IFlows.EngineerFlow), Ct);
        var startedFlow = Assert.Single(activeFlows);
        var flowId = startedFlow.FlowId;

        // 2. Drive flow.engineer through brainstorm -> 2 parallel worktrees -> Roslyn AST edits -> Release dotnet build -> live-verify -> approval-gate.
        FlowView? waiting = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await flows.Read(flowId, Ct);
            if (snapshot is { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed })
            {
                waiting = snapshot;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(waiting);
        Assert.True(
            waiting!.Status == FlowStatus.Waiting,
            $"Expected flow.engineer to park at approval-gate, got {waiting.Status}: {waiting.Detail}");
        Assert.Equal("approval-gate", waiting.WaitingStep);
        Assert.NotNull(waiting.ConfirmationPlanId);
        Assert.StartsWith("app:engineer:", waiting.ConfirmationPlanId!, StringComparison.Ordinal);

        // Verify both parallel ContainerVolume worktrees compiled MathKit.csproj in Release via ISandboxCommandRunner and passed Roslyn + Live checks.
        var starter = os.Get<IFlowStarter>(NeuronId.Parse(flowId));
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
                Assert.True(inst.GetProperty("build").GetProperty("dotnetBuildPassed").GetBoolean());
                Assert.Equal(0, inst.GetProperty("build").GetProperty("warningCount").GetInt32());
                Assert.Equal(0, inst.GetProperty("build").GetProperty("errorCount").GetInt32());
                Assert.True(inst.GetProperty("live").GetProperty("succeeded").GetBoolean());

                var leaseTree = inst.GetProperty("lease").GetProperty("treePath").GetString()!;
                Assert.True(Directory.Exists(leaseTree), $"Worktree #{idx} must exist while waiting at approval-gate.");
            }
        }

        Assert.True(sandboxInvocations.Count >= 2, $"Expected ISandboxCommandRunner to execute dotnet build for both parallel worktrees; got {sandboxInvocations.Count}.");
        Assert.All(sandboxInvocations, static req =>
        {
            Assert.Equal("dotnet", req.Executable);
            Assert.Contains("modules/MathKit/MathKit.csproj", req.Arguments);
        });

        // Nothing is pushed to bare origin before the human owner approves the SHA-bound plan.
        Assert.Equal(string.Empty, RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim());

        // Sync EngineerApp with the live waiting flow and verify its UI neurons reflect the active pipeline.
        await engineerScreen.Request(new EngineerOpen(FlowId: flowId));
        await os.Idle();
        var engStepper = os.Get<IStepper>(new NeuronId(brain, "ui.stepper", EngineerApp.StepperKey));
        var engTree = os.Get<ITreeView>(new NeuronId(brain, "ui.tree-view", EngineerApp.WorktreeTreeKey));
        Assert.Equal("review-and-pr", (await engStepper.Read()).ActiveStepId);
        Assert.True((await engTree.Read()).Nodes.Length >= 4);

        // 3. Open app.creator in the SAME brain, inspect the live flow & causal signal trace, and time-travel with CreatorReplay + IPropertyGrid!
        var creatorId = new NeuronId(brain, "app.creator", "default");
        var creatorScreen = os.Get<IScreen>(creatorId);
        await creatorScreen.Request(new CreatorOpen());
        await creatorScreen.Request(new CreatorRun(flowId));
        await os.Idle();

        var creatorView = await creatorScreen.Read();
        Assert.NotNull(creatorView);
        var creatorViewRaw = creatorView!.View.GetRawText();
        Assert.Contains(flowId, creatorViewRaw, StringComparison.Ordinal);
        Assert.Contains("Add Subtract and Clamp helpers to Calculator", creatorViewRaw, StringComparison.Ordinal);

        await creatorScreen.Request(new CreatorTrace(waiting.TraceId));
        await os.Idle();

        var replaySlider = os.Get<ISlider>(new NeuronId(brain, "ui.slider", "creator/replay/slider"));
        var signalsTable = os.Get<ITable>(new NeuronId(brain, "ui.table", "creator/inspector/signals"));
        var stateGrid = os.Get<IPropertyGrid>(new NeuronId(brain, "ui.property-grid", "creator/inspector/state"));

        var initialSlider = await replaySlider.Read();
        Assert.True(initialSlider.Max >= 4, $"Expected multiple causal flow signals on Creator timeline; got max={initialSlider.Max}.");
        Assert.True((await signalsTable.Read()).Total >= 4);

        // Step to First event -> Next event -> Previous event -> Last event, verifying IPropertyGrid ('creator/inspector/state') updates live!
        await creatorScreen.Request(new CreatorReplay("first"));
        await os.Idle();
        var gridAtFirst = await stateGrid.Read();
        Assert.StartsWith("State · Event 1 / ", gridAtFirst.Title, StringComparison.Ordinal);
        Assert.Contains("\"eventIndex\":1", gridAtFirst.RawJson, StringComparison.Ordinal);
        Assert.NotEmpty(gridAtFirst.Entries);

        await creatorScreen.Request(new CreatorReplay("next"));
        await os.Idle();
        var gridAtSecond = await stateGrid.Read();
        Assert.StartsWith("State · Event 2 / ", gridAtSecond.Title, StringComparison.Ordinal);
        Assert.Contains("\"eventIndex\":2", gridAtSecond.RawJson, StringComparison.Ordinal);
        Assert.NotEqual(gridAtFirst.RawJson, gridAtSecond.RawJson);

        await creatorScreen.Request(new CreatorReplay("prev"));
        await os.Idle();
        var gridBackAtFirst = await stateGrid.Read();
        Assert.Equal(gridAtFirst.Title, gridBackAtFirst.Title);
        Assert.Equal(gridAtFirst.RawJson, gridBackAtFirst.RawJson);

        await creatorScreen.Request(new CreatorReplay("last"));
        await os.Idle();
        var gridAtLast = await stateGrid.Read();
        Assert.Contains("/", gridAtLast.Title, StringComparison.Ordinal);
        Assert.NotEqual(gridAtFirst.RawJson, gridAtLast.RawJson);

        // 4. Approve the SHA-bound confirmation gate as the brain owner, advance push -> pr -> clean, and verify bare remote + cleanup!
        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var pendingItem = Assert.Single((await inbox.Read()).Pending, p => p.PlanId == waiting.ConfirmationPlanId);
        Assert.Equal(EngineerFlow.ConfirmationKind, pendingItem.Plan.Kind);
        await inbox.Resolve(waiting.ConfirmationPlanId!, approve: true);

        FlowView? completed = null;
        for (var poll = 0; poll < 160; poll++)
        {
            await os.Idle();
            var snapshot = await flows.Read(flowId, Ct);
            if (snapshot is { Status: FlowStatus.Completed or FlowStatus.Failed })
            {
                completed = snapshot;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(completed);
        Assert.True(
            completed!.Status == FlowStatus.Completed,
            $"Expected flow.engineer to complete after owner approval, got {completed.Status}: {completed.Detail}");
        Assert.Equal(EngineerFlow.Names, completed.Done);

        var headAfter = await starter.Head();
        using var afterDoc = JsonDocument.Parse(headAfter.InnerJson);
        var rootAfter = afterDoc.RootElement;
        Assert.Equal("owner", rootAfter.GetProperty("approval").GetProperty("approvedBy").GetString());
        Assert.True(rootAfter.GetProperty("publication").GetProperty("pullRequestCompleted").GetBoolean());
        Assert.True(rootAfter.GetProperty("publication").GetProperty("cleaned").GetBoolean());

        var pushedRef = rootAfter.GetProperty("publication").GetProperty("pushedRef").GetString()!;
        var selectedIdx = rootAfter.GetProperty("selectedInstanceIndex").GetInt32();
        var instancesAfter = rootAfter.GetProperty("instances");
        string? expectedWinningSha = null;

        for (var idx = 0; idx < instancesAfter.GetArrayLength(); idx++)
        {
            var inst = instancesAfter[idx];
            var treePath = inst.GetProperty("lease").GetProperty("treePath").GetString()!;
            Assert.False(Directory.Exists(treePath), $"Parallel worktree #{idx} ('{treePath}') must be cleaned up after publish.");
            if (inst.GetProperty("instanceIndex").GetInt32() == selectedIdx)
            {
                expectedWinningSha = inst.GetProperty("commit").GetProperty("commit").GetString();
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(expectedWinningSha));
        var remoteCommitSha = RunGit(bareOrigin, ["rev-parse", pushedRef]).Trim();
        Assert.Equal(expectedWinningSha, remoteCommitSha);

        var committedSource = RunGit(bareOrigin, ["show", $"{remoteCommitSha}:modules/MathKit/Calculator.cs"]);
        Assert.Contains("public static int Subtract(int a, int b)", committedSource, StringComparison.Ordinal);
        Assert.Contains("public static int Clamp(int value, int min, int max)", committedSource, StringComparison.Ordinal);

        // Sync EngineerApp & CreatorApp after completion so all 3 OS surfaces reflect the completed run.
        await engineerScreen.Request(new EngineerOpen(FlowId: flowId));
        await creatorScreen.Request(new CreatorOpen());
        await os.Idle();
        Assert.All((await engStepper.Read()).Steps, static step => Assert.Equal(StepperStatuses.Done, step.Status));
        Assert.Empty(await inbox.Read() is var finalInbox ? finalInbox.Pending : []);
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

    private sealed class DogfoodTwoTaskPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Apply two sequential Roslyn AST edits to MathKit.Calculator across isolated container-volume worktrees.",
                CriticReview: "Verify Triple Git Lock, zero Roslyn diagnostics, zero Release compiler warnings, and SHA-bound owner approval.",
                Transcript:
                [
                    new("User", "user", "Goal", goal),
                    new("Architect", "architect", "Worktree Plan", "Execute t1-subtract then t2-clamp on modules/MathKit/Calculator.cs."),
                    new("Critic", "critic", "Quality Gates", "Compile MathKit.csproj in Release via ISandboxCommandRunner and verify 0 errors."),
                ],
                Tasks:
                [
                    new("t1-subtract", "Add Subtract method via Roslyn AST", "csharp", ["modules/MathKit/Calculator.cs"], [], "Add Subtract"),
                    new("t2-clamp", "Add Clamp method via Roslyn AST", "csharp", ["modules/MathKit/Calculator.cs"], ["t1-subtract"], "Add Clamp"),
                ]));
    }

    private sealed class DogfoodRoslynCoder(IWorktreeRoslyn roslyn) : ICoder
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
                "roslyn-ast-dogfood");
        }
    }

    private sealed class AuditingSandboxCommandRunner(
        ISandboxCommandRunner inner,
        List<SandboxCommandRequest> sink) : ISandboxCommandRunner
    {
        public IReadOnlyList<string> BuildContainerArguments(WorkTreeLease lease, SandboxCommandRequest request)
            => inner.BuildContainerArguments(lease, request);

        public Task<SandboxCommandResult> ExecuteAsync(WorkTreeLease lease, SandboxCommandRequest request, CancellationToken ct = default)
        {
            // Verify container CLI arguments also build cleanly for every ContainerVolume verification invocation.
            var containerArgs = inner.BuildContainerArguments(lease, request);
            Assert.Contains("--rm", containerArgs);
            Assert.Contains("none", containerArgs);

            lock (sink)
            {
                sink.Add(request);
            }

            return inner.ExecuteAsync(lease, request, ct);
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
