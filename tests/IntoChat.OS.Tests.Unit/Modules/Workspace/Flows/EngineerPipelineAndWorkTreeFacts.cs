using System.Diagnostics;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class EngineerPipelineAndWorkTreeFacts : IDisposable
{
    private readonly string _root;

    public EngineerPipelineAndWorkTreeFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "engineer-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private (string BareOriginPath, string SourceCheckoutPath, string HomeDir) CreateBareOriginAndCheckout(string subName)
    {
        var subRoot = Path.Combine(_root, subName);
        var bareOrigin = Path.Combine(subRoot, "origin.git");
        var checkout = Path.Combine(subRoot, "source-repo");
        var home = Path.Combine(subRoot, "home");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(home);

        RunGit(bareOrigin, ["init", "--bare", "--initial-branch=main"]);
        RunGit(checkout, ["init", "--initial-branch=main"]);
        RunGit(checkout, ["config", "user.name", "Repo Owner"]);
        RunGit(checkout, ["config", "user.email", "owner@example.test"]);

        Directory.CreateDirectory(Path.Combine(checkout, "code"));
        File.WriteAllText(Path.Combine(checkout, "code", "Calculator.cs"), """
            namespace Demo;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        RunGit(checkout, ["add", "."]);
        RunGit(checkout, ["commit", "-m", "Initial commit"]);
        RunGit(checkout, ["remote", "add", "origin", bareOrigin]);
        RunGit(checkout, ["push", "-u", "origin", "main"]);

        return (bareOrigin, checkout, home);
    }

    [Theory]
    [InlineData(WorkTreeBackendKind.LocalGit)]
    [InlineData(WorkTreeBackendKind.ContainerVolume)]
    public async Task WorkTreeServiceSupportsNParallelIsolatedLeasesAcrossLocalGitAndContainerVolume(WorkTreeBackendKind backend)
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout($"parallel-{backend}");
        var volumeRoot = Path.Combine(_root, $"volumes-{backend}");

        var service = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VolumeMountRoot = volumeRoot,
            Backend = backend,
        });

        var repoEntry = backend == WorkTreeBackendKind.ContainerVolume
            ? new RegisteredRepository("os", checkout, DefaultBaseBranch: "main", RemoteCloneUrl: bareOrigin)
            : new RegisteredRepository("os", checkout, DefaultBaseBranch: "main");
        service.RegisterRepository(repoEntry);

        // Long runId (> 8 chars) proves parallel branches never collide after shortRun truncation
        var leases = await service.PrepareParallelAsync(RepositoryId.Parse("os"), "longrunidentifier99", "Add parallel helpers", instanceCount: 3, ct: Ct);

        Assert.Equal(3, leases.Count);
        Assert.Equal(3, leases.Select(static l => l.EffectiveLeaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, leases.Select(static l => l.TreePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, leases.Select(static l => l.BranchName).Distinct(StringComparer.Ordinal).Count());

        for (var i = 0; i < leases.Count; i++)
        {
            var lease = leases[i];
            Assert.Equal(i, lease.InstanceIndex);
            Assert.Equal(backend, lease.Backend);
            Assert.EndsWith($"-w{i + 1}", lease.BranchName, StringComparison.Ordinal);
            if (backend == WorkTreeBackendKind.ContainerVolume)
            {
                Assert.NotNull(lease.VolumeId);
                Assert.StartsWith(Path.GetFullPath(volumeRoot), Path.GetFullPath(lease.TreePath), StringComparison.OrdinalIgnoreCase);
            }

            await WorkTreeService.VerifyTripleGitLockAsync(lease, Ct);
            await File.WriteAllTextAsync(Path.Combine(lease.TreePath, "code", "Calculator.cs"), $$"""
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                    public static int Candidate{{i}}(int x) => x + {{i + 1}};
                }
                """, Ct);
        }

        var commits = await Task.WhenAll(leases.Select((l, idx) =>
            service.VerifyAndCommitAsync(l, $"feat(ino): Candidate {idx} edit", Ct)));
        Assert.Equal(3, commits.Select(static c => c.Commit.Value).Distinct(StringComparer.Ordinal).Count());

        // Publish winning candidate #1 to bare origin
        var pushedRef = await service.PushAsync(leases[1], commits[1].Commit, Ct);
        Assert.Equal($"refs/heads/{leases[1].BranchName}", pushedRef);
        Assert.Equal(commits[1].Commit.Value, RunGit(bareOrigin, ["rev-parse", pushedRef]).Trim());

        // Idempotent cleanup of all 3 parallel leases
        await service.CleanupParallelAsync(leases, Ct);
        await service.CleanupParallelAsync(leases, Ct);
        Assert.All(leases, static l => Assert.False(Directory.Exists(l.TreePath)));
    }

    [Fact]
    public void EngineerTaskPlanValidatesTopologicalDagAndRejectsCyclesAndFileBudgetViolations()
    {
        var valid = new EngineerTaskPlan(
            Goal: "Build feature",
            ArchitectureSummary: "Clean DDD",
            CriticReview: "Safe",
            Transcript: [new("User", "user", "Goal", "Build feature")],
            Tasks:
            [
                new("t2", "Second step", "dart", ["ui/widget.dart"], ["t1"], "Align widget"),
                new("t1", "First step", "csharp", ["code/Domain.cs"], [], "Add domain"),
            ]);

        Assert.Null(valid.Validate(maxChangedFiles: 5));
        Assert.Equal(["t1", "t2"], valid.TopologicalOrder().Select(static t => t.Id));

        var cyclic = valid with
        {
            Tasks =
            [
                new("a", "Step A", "csharp", ["code/A.cs"], ["b"], "A"),
                new("b", "Step B", "csharp", ["code/B.cs"], ["a"], "B"),
            ],
        };
        Assert.Contains("cycle", cyclic.Validate(5), StringComparison.OrdinalIgnoreCase);

        var overBudget = valid with
        {
            Tasks =
            [
                new("only", "Too many files", "csharp", ["code/F1.cs", "code/F2.cs", "code/F3.cs"], [], "Edit"),
            ],
        };
        Assert.Contains("exceeding the policy maximum of 2", overBudget.Validate(maxChangedFiles: 2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalLlmCoderExecutesRoslynSymbolsEditMemberAndDiagnosticsTools()
    {
        var treeDir = Path.Combine(_root, "coder-roslyn-wt");
        Directory.CreateDirectory(Path.Combine(treeDir, "code"));
        await File.WriteAllTextAsync(Path.Combine(treeDir, ".git"), "gitdir: C:/fake/wt\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(treeDir, "code", "ServiceCalc.cs"), """
            namespace Demo;

            public static class ServiceCalc
            {
                public static int Double(int x) => x * 2;
            }
            """, Ct);

        var turns = new Queue<Func<InferenceRequest, AiMessage>>(
        [
            _ => new AiMessage("assistant",
            [
                new AiToolCall("c1", "roslyn_symbols", "{}"),
            ]),
            req =>
            {
                var symbolJson = req.Messages[^1].Content.OfType<AiToolResult>().Single().ResultJson;
                Assert.Contains("ServiceCalc", symbolJson, StringComparison.Ordinal);
                return new AiMessage("assistant",
                [
                    new AiToolCall(
                        "c2",
                        "roslyn_edit_member",
                        """{"kind":"add_or_replace_member","path":"code/ServiceCalc.cs","type_name":"ServiceCalc","member_name":"Triple","source":"public static int Triple(int x) => x * 3;"}"""),
                ]);
            },
            req =>
            {
                var editJson = req.Messages[^1].Content.OfType<AiToolResult>().Single().ResultJson;
                Assert.Contains("\"ok\":true", editJson, StringComparison.Ordinal);
                return new AiMessage("assistant",
                [
                    new AiToolCall("c3", "roslyn_diagnostics", """{"path":"code/ServiceCalc.cs"}"""),
                ]);
            },
            _ => new AiMessage("assistant", [new AiText("Added Triple via Roslyn AST edit with zero diagnostics.")]),
        ]);

        var services = new ServiceCollection()
            .AddSingleton<IModelInference>(new QueueInference(turns))
            .AddSingleton<IWorktreeRoslyn>(new WorktreeRoslynAnalyzer())
            .BuildServiceProvider();

        var coder = new LocalLlmCoder(services);
        var lease = new WorkTreeLease(
            "run-roslyn",
            "os",
            treeDir,
            treeDir,
            treeDir,
            new string('a', 64),
            "main",
            new string('0', 40),
            "ino/roslyn-edit-run");

        var edit = await coder.EditAsync(lease, "Add Triple to ServiceCalc", Ct);
        Assert.Equal(["code/ServiceCalc.cs"], edit.TouchedFiles);
        var onDisk = await File.ReadAllTextAsync(Path.Combine(treeDir, "code", "ServiceCalc.cs"), Ct);
        Assert.Contains("public static int Triple(int x) => x * 3;", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineerRunEnforcesImmutableHumanShaBoundPublishInvariants()
    {
        foreach (var prop in typeof(EngineerRun).GetProperties())
        {
            Assert.True(prop.SetMethod is null || !prop.SetMethod.IsPublic, $"EngineerRun.{prop.Name} must not have a public setter.");
        }

        var sha = CommitSha.Parse(new string('d', 40));
        var lease = new WorkTreeLease("r1", "os", "/repo", "/wt/0", "/repo/.git/wt/0", new string('c', 64), "main", new string('0', 40), "ino/feat-r1", "intochat/os", "https://github.com/intochat/os.git");
        var plan = new EngineerTaskPlan("Goal", "Arch", "Critic", [], [new("t1", "Task", "csharp", ["code/Calculator.cs"], [], "Do")]);
        var commit = ChangeSet.FromDiff(sha, [RepoPath.Parse("code/Calculator.cs")], "--- a/code/Calculator.cs\n+++ b/code/Calculator.cs\n@@ -1 +1 @@\n-a\n+b\n");
        var buildOk = new EngineerBuildReport(true, true, true, true, 5, 0, 0, 0, [], 100, "OK");
        var liveOk = new EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], [], "OK");

        var ready = EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal", instanceCount: 1)
            .WithPlan(plan)
            .WithLeases("r1", [lease])
            .WithUpdatedInstances([new EngineerInstance(0, lease, ["t1"], new CoderEdit("Done", "m", [RepoPath.Parse("code/Calculator.cs")]), [], commit, buildOk, liveOk, "verified")], 0)
            .WithApprovalPending("app:engineer:r1:" + sha.Value, DateTimeOffset.UtcNow.AddHours(24));

        var agent = ConfirmationPrincipal.Agent("ino");
        var human = ConfirmationPrincipal.Person("person-1");

        Assert.Throws<SecurityException>(() => ready.EnsureCanPublish(
            new ConfirmationResult("p", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false) { Approver = agent },
            sha));

        Assert.Equal("person-1", ready.EnsureCanPublish(
            new ConfirmationResult("p", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false) { Approver = human },
            sha));
    }

    [Fact]
    public async Task EngineerFlowExecutesEndToEndAcrossTwoParallelWorktreesAndPublishesOnPersonApproval()
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout("e2e-flow");
        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            Backend = WorkTreeBackendKind.ContainerVolume,
            VolumeMountRoot = Path.Combine(_root, "e2e-volumes"),
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main", RemoteCloneUrl: bareOrigin));

        var taskInvocations = new List<(string LeaseId, string Prompt)>();
        var coder = new CheckpointingRoslynCoder(taskInvocations);
        var planner = new TwoStepPlanner();
        var buildVerifier = new DeterministicBuildVerifier();
        var liveVerifier = new DeterministicLiveVerifier();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IEngineerPlanner>(planner);
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(buildVerifier);
        serviceCollection.AddSingleton<ILiveVerifier>(liveVerifier);
        serviceCollection.AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer());
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner")));
        os.RegisterCatalog();

        var flows = os.Get<IFlows>(IFlows.Key("owner"));
        var startJson = JsonSerializer.Serialize(new
        {
            runId = "engflow01",
            repository = "os",
            goal = "Add Subtract and Modulo methods to Calculator",
            instanceCount = 2,
        }, EngineerFlow.Json);

        var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Parallel Calculator Upgrade", null, startJson, Ct, flowId: "engflow01");
        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var check = await flows.Read(flowId, Ct);
            if (check is { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed })
            {
                break;
            }

            await Task.Delay(50, Ct);
        }

        var waiting = await flows.Read(flowId, Ct);
        Assert.NotNull(waiting);
        Assert.True(waiting!.Status == FlowStatus.Waiting, $"Expected Waiting at approval-gate, got {waiting.Status}: {waiting.Detail}");
        Assert.Equal("approval-gate", waiting.WaitingStep);
        Assert.StartsWith("app:engineer:engflow01:", waiting.ConfirmationPlanId!, StringComparison.Ordinal);

        // Inspect persisted flow state before human approval: 2 instances, both tasks checkpointed, build + live passed, nothing pushed yet!
        var starter = os.Get<IFlowStarter>(NeuronId.Parse(flowId));
        var headBefore = await starter.Head();
        var runBefore = JsonSerializer.Deserialize<EngineerRun>(headBefore.InnerJson, EngineerFlow.Json)!;

        Assert.Equal(2, runBefore.Instances.Length);
        Assert.All(runBefore.Instances, static inst =>
        {
            Assert.Equal(["t1-sub", "t2-mod"], inst.CompletedTaskIds);
            Assert.True(inst.Build?.Succeeded);
            Assert.True(inst.Live?.Succeeded);
            Assert.NotNull(inst.Commit);
        });
        Assert.Equal("", RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim());

        // Approve as human Person through TestOS confirmations
        var inbox = os.Get<IConfirmationInbox>(new NeuronId(new BrainId("owner"), "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        await inbox.Resolve(waiting.ConfirmationPlanId!, approve: true);

        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var check = await flows.Read(flowId, Ct);
            if (check is { Status: FlowStatus.Completed or FlowStatus.Failed })
            {
                break;
            }

            await Task.Delay(50, Ct);
        }

        var completed = await flows.Read(flowId, Ct);
        Assert.NotNull(completed);
        Assert.Equal(FlowStatus.Completed, completed!.Status);
        Assert.Equal(EngineerFlow.Names, completed.Done);

        var runAfter = JsonSerializer.Deserialize<EngineerRun>((await starter.Head()).InnerJson, EngineerFlow.Json)!;
        var winner = runAfter.SelectedInstance!;
        Assert.Equal("owner", runAfter.Approval?.ApprovedBy);
        Assert.Equal($"refs/heads/{winner.Lease!.BranchName}", runAfter.Publication?.PushedRef);
        Assert.True(runAfter.Publication?.PullRequestCompleted);
        Assert.True(runAfter.Publication?.Cleaned);
        Assert.All(runAfter.Instances, static i => Assert.False(Directory.Exists(i.Lease!.TreePath)));

        // Verify bare remote received the exact SHA-bound winning commit
        var remoteSha = RunGit(bareOrigin, ["rev-parse", runAfter.Publication!.PushedRef!]).Trim();
        Assert.Equal(winner.Commit!.Commit.Value, remoteSha);
    }

    [Fact]
    public async Task EngineerBuildVerifierRoutesContainerVolumeLeaseCommandsThroughSandboxCommandRunner()
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout("sbx-verifier");
        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            Backend = WorkTreeBackendKind.ContainerVolume,
            VolumeMountRoot = Path.Combine(_root, "sbx-verifier-volumes"),
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main", RemoteCloneUrl: bareOrigin));

        var lease = await workTree.PrepareAsync(RepositoryId.Parse("os"), "verifyvol1", "Verify container sandbox routing", null, Ct);
        await File.WriteAllTextAsync(
            Path.Combine(lease.WorkspacePath, "code", "Calc.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>",
            Ct);
        Directory.CreateDirectory(Path.Combine(lease.WorkspacePath, "ui", "lib"));
        await File.WriteAllTextAsync(Path.Combine(lease.WorkspacePath, "ui", "pubspec.yaml"), "name: demo_ui\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(lease.WorkspacePath, "ui", "lib", "badge.dart"), "class Badge {}\n", Ct);

        var recordedRequests = new List<SandboxCommandRequest>();
        var fakeRunner = new RecordingSandboxRunner(recordedRequests);
        var verifier = new EngineerBuildVerifier(configuration: null, sandboxRunner: fakeRunner);

        var report = await verifier.VerifyAsync(lease, ["code/Calculator.cs", "ui/lib/badge.dart"], Ct);

        Assert.True(report.Succeeded, report.Summary);
        Assert.True(report.DotnetBuildPassed);
        Assert.True(report.FlutterAnalyzePassed);
        Assert.Equal(2, recordedRequests.Count);
        Assert.Equal("dotnet", recordedRequests[0].Executable);
        Assert.Contains("code/Calc.csproj", recordedRequests[0].Arguments);
        Assert.Null(recordedRequests[0].RelativeWorkingDirectory);

        Assert.Equal("flutter", recordedRequests[1].Executable);
        Assert.Equal(["analyze", "--no-pub"], recordedRequests[1].Arguments);
        Assert.Equal("ui", recordedRequests[1].RelativeWorkingDirectory);

        await workTree.CleanupAsync(lease, Ct);
    }

    // flow.change starts nothing any more: the run is one a brain already holds, stored as running, and it still finishes.
    [Fact]
    public async Task FeatureToPrFlowAdapterExecutesConsolidatedPipelineStagesEndToEndOnTestOS()
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout("f2pr-consolidated");
        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VerifyFormatting = false,
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));

        var coder = new SingleFileRoslynCoder();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner")));
        os.RegisterCatalog();

        var flows = os.Get<IFlows>(IFlows.Key("owner"));
        var startJson = JsonSerializer.Serialize(
            Change.Start("f2pr01", RepositoryId.Parse("os"), "Add Multiply via consolidated adapter"),
            FeatureToPrFlow.Json);
        var refused = await Assert.ThrowsAsync<NotSupportedException>(
            () => flows.Start(FeatureToPrFlow.Alias, "Consolidated Change Run", null, startJson, Ct, flowId: "refused01"));
        Assert.Equal(FeatureToPrFlow.Retired, refused.Message);

        var flowNeuron = new NeuronId(new BrainId("owner"), FeatureToPrFlow.Alias, "f2pr01");
        var flowId = flowNeuron.ToString();
        os.SeedStoredState(flowNeuron, JsonSerializer.Serialize(
            new FlowState<Change>() with
            {
                FlowId = flowId,
                Title = "Consolidated Change Run",
                Owner = "owner",
                TraceId = "trace-f2pr01",
                Status = FlowStatus.Running,
                Started = DateTimeOffset.UnixEpoch,
                Inner = Change.Start("f2pr01", RepositoryId.Parse("os"), "Add Multiply via consolidated adapter"),
            },
            DigitalBrain.Invocation.RuntimeProtocol.Json));
        _ = await os.Get<IFlowStarter>(flowNeuron).Head();
        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var check = await flows.Read(flowId, Ct);
            if (check is { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed })
            {
                break;
            }

            await Task.Delay(50, Ct);
        }

        var waiting = await flows.Read(flowId, Ct);
        Assert.NotNull(waiting);
        Assert.Equal(FlowStatus.Waiting, waiting!.Status);
        Assert.Equal("approval-gate", waiting.WaitingStep);
        Assert.StartsWith("app:change:f2pr01:", waiting.ConfirmationPlanId!, StringComparison.Ordinal);

        var inbox = os.Get<IConfirmationInbox>(new NeuronId(new BrainId("owner"), "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var pendingReq = Assert.Single((await inbox.Read()).Pending, p => p.PlanId == waiting.ConfirmationPlanId);
        Assert.Equal(FeatureToPrFlow.ConfirmationKind, pendingReq.Plan.Kind);
        await inbox.Resolve(waiting.ConfirmationPlanId!, approve: true);

        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var check = await flows.Read(flowId, Ct);
            if (check is { Status: FlowStatus.Completed or FlowStatus.Failed })
            {
                break;
            }

            await Task.Delay(50, Ct);
        }

        var completed = await flows.Read(flowId, Ct);
        Assert.NotNull(completed);
        Assert.Equal(FlowStatus.Completed, completed!.Status);
        Assert.Equal(FeatureToPrFlow.Names, completed.Done);

        var starter = os.Get<IFlowStarter>(NeuronId.Parse(flowId));
        var changeAfter = JsonSerializer.Deserialize<Change>((await starter.Head()).InnerJson, FeatureToPrFlow.Json)!;
        Assert.Equal("owner", changeAfter.Approval?.ApprovedBy);
        Assert.True(changeAfter.Publication?.Cleaned);
        Assert.False(Directory.Exists(changeAfter.Tree!.TreePath));
        Assert.Equal(changeAfter.Commit!.Commit.Value, RunGit(bareOrigin, ["rev-parse", changeAfter.Publication!.PushedRef!]).Trim());
    }

    [Fact]
    public async Task EngineerPlannerRunsDistinctArchitectAndCriticTurnsAndRefinesTaskPlan()
    {
        var seenPrompts = new List<string>();
        var turns = new Queue<Func<InferenceRequest, AiMessage>>(
        [
            req =>
            {
                var prompt = string.Join("\n", req.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text));
                seenPrompts.Add(prompt);
                Assert.Contains("ARCHITECT", prompt, StringComparison.Ordinal);
                return new AiMessage("assistant",
                [
                    new AiText("""
                        {
                          "goal": "Add Subtract and Clamp to Calculator",
                          "architectureSummary": "Architect draft: split Subtract and Clamp into sequential Roslyn AST steps.",
                          "tasks": [
                            { "id": "t1-subtract", "title": "Draft Subtract", "language": "csharp", "targetFiles": ["code/Calculator.cs"], "dependsOn": [], "instructions": "Add Subtract" }
                          ]
                        }
                        """),
                ]);
            },
            req =>
            {
                var prompt = string.Join("\n", req.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text));
                seenPrompts.Add(prompt);
                Assert.Contains("CRITIC", prompt, StringComparison.Ordinal);
                Assert.Contains("Architect draft: split Subtract and Clamp", prompt, StringComparison.Ordinal);
                return new AiMessage("assistant",
                [
                    new AiText("""
                        {
                          "goal": "Add Subtract and Clamp to Calculator",
                          "architectureSummary": "Refined DDD worktree plan across Calculator.cs within 5-file policy.",
                          "criticReview": "Critic verified HouseRules, zero swallowed exceptions, and added dependent t2-clamp task.",
                          "tasks": [
                            { "id": "t1-subtract", "title": "Implement Subtract via Roslyn AST", "language": "csharp", "targetFiles": ["code/Calculator.cs"], "dependsOn": [], "instructions": "Add Subtract(int a, int b)" },
                            { "id": "t2-clamp", "title": "Implement Clamp with min/max bounds", "language": "csharp", "targetFiles": ["code/Calculator.cs"], "dependsOn": ["t1-subtract"], "instructions": "Add Clamp(int v, int min, int max)" }
                          ]
                        }
                        """),
                ]);
            },
        ]);

        await using var sp = new ServiceCollection()
            .AddSingleton<IModelInference>(new QueueInference(turns))
            .BuildServiceProvider();
        var planner = new EngineerPlanner(sp);

        var plan = await planner.PlanAsync("os", "Add Subtract and Clamp to Calculator", [".cs", ".dart"], 5, Ct);

        Assert.Equal(2, seenPrompts.Count);
        Assert.Null(plan.Validate(5));
        Assert.Equal(["t1-subtract", "t2-clamp"], plan.TopologicalOrder().Select(static t => t.Id));
        Assert.Contains("Critic verified HouseRules", plan.CriticReview, StringComparison.Ordinal);
        Assert.Contains(plan.Transcript, static t => t.Role == "user");
        Assert.Contains(plan.Transcript, static t => t.Role == "architect");
        Assert.Contains(plan.Transcript, static t => t.Role == "critic");
    }

    [Fact]
    public async Task EngineerLiveVerifierAuditsOpenTelemetryErrorSpansTraceReaderAndLiveScreenHealth()
    {
        var treeDir = Path.Combine(_root, "live-verify-wt");
        Directory.CreateDirectory(treeDir);

        var liveConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Engineer:LiveBaseUrl"] = "http://127.0.0.1:19877",
            })
            .Build();
        var liveHttp = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var verifier = new EngineerLiveVerifier(configuration: liveConfig, httpClientFactory: liveHttp);
        var cleanTrace = new FakeTraceReader(
        [
            new TraceEntry("owner/flow.engineer/r1", 1, "flow.started", DateTimeOffset.UtcNow, "trace-clean", null, "{\"title\":\"Run\"}"),
            new TraceEntry("owner/flow.engineer/r1", 2, "flow.step", DateTimeOffset.UtcNow, "trace-clean", "1", "{\"step\":\"live-verify\"}"),
        ]);
        var healthyScreen = new FakeVerifyScreen(
            JsonDocument.Parse("""{"kind":"appShell","children":[{"kind":"banner","tone":"info","title":"Healthy"}]}""").RootElement.Clone());

        var healthyReport = await verifier.VerifyLiveAsync(
            treeDir,
            "Verify healthy telemetry and screen",
            ["code/Calculator.cs"],
            new EngineerLiveContext("trace-clean", cleanTrace, [("app.engineer", healthyScreen)]),
            Ct);

        Assert.True(healthyReport.Succeeded, healthyReport.Summary);
        Assert.Equal(0, healthyReport.ErrorSpanCount);
        Assert.Equal(0, healthyReport.ErrorLogCount);
        Assert.Contains(healthyReport.Probes, static p => p.Name == "otel-activity-spans" && p.Passed);
        Assert.Contains(healthyReport.Probes, static p => p.Name == "brain-trace-reader" && p.Passed);
        Assert.Contains(healthyReport.Probes, static p => p.Name == "mcp-screen-app.engineer" && p.Passed);

        // Emit an OpenTelemetry Error span and an error UI banner: EngineerLiveVerifier must fail and report both!
        verifier.ClearCapturedSpans();
        using (var errSpan = EngineerTelemetry.Source.StartActivity("engineer.synthetic-error"))
        {
            errSpan?.SetTag("brain.trace_id", "trace-bad");
            errSpan?.SetStatus(ActivityStatusCode.Error, "Simulated telemetry failure");
        }

        var errorScreen = new FakeVerifyScreen(
            JsonDocument.Parse("""{"kind":"appShell","children":[{"kind":"infoBar","tone":"error","title":"Crash"}]}""").RootElement.Clone());

        var failingReport = await verifier.VerifyLiveAsync(
            treeDir,
            "Verify error span and error node detection",
            ["code/Calculator.cs"],
            new EngineerLiveContext("trace-bad", cleanTrace, [("app.engineer", errorScreen)]),
            Ct);

        Assert.False(failingReport.Succeeded);
        Assert.True(failingReport.ErrorSpanCount >= 1);
        Assert.True(failingReport.ErrorLogCount >= 1);
        Assert.False(failingReport.McpProbesPassed);
        Assert.Contains(failingReport.Probes, static p => p.Name == "otel-activity-spans" && !p.Passed);
        Assert.Contains(failingReport.Probes, static p => p.Name == "mcp-screen-app.engineer" && !p.Passed);
    }

    [Fact]
    public async Task EngineerLiveVerifierIgnoresErrorSpansOfOtherTraces()
    {
        var treeDir = Path.Combine(_root, "live-verify-other-trace");
        Directory.CreateDirectory(treeDir);

        var liveConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Engineer:LiveBaseUrl"] = "http://127.0.0.1:19877",
            })
            .Build();
        var liveHttp = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var verifier = new EngineerLiveVerifier(configuration: liveConfig, httpClientFactory: liveHttp);
        var cleanTrace = new FakeTraceReader(
        [
            new TraceEntry("owner/flow.engineer/r2", 1, "flow.started", DateTimeOffset.UtcNow, "trace-mine", null, "{\"title\":\"Run\"}"),
        ]);
        var healthyScreen = new FakeVerifyScreen(
            JsonDocument.Parse("""{"kind":"appShell","children":[{"kind":"banner","tone":"info","title":"Healthy"}]}""").RootElement.Clone());

        // Another brain's call fails in the same process (another agent, another test): its error span carries its own trace.
        verifier.ClearCapturedSpans();
        using (var other = EngineerTelemetry.Source.StartActivity("engineer.unrelated-failure"))
        {
            other?.SetTag("brain.trace_id", "trace-someone-else");
            other?.SetStatus(ActivityStatusCode.Error, "Unrelated failure");
        }

        var report = await verifier.VerifyLiveAsync(
            treeDir,
            "Verify only this run's spans count",
            ["code/Calculator.cs"],
            new EngineerLiveContext("trace-mine", cleanTrace, [("app.engineer", healthyScreen)]),
            Ct);

        Assert.True(report.Succeeded, report.Summary);
        Assert.Equal(0, report.ErrorSpanCount);
    }

    [Fact]
    public async Task FlowsNeuronAndInoStudioDelegateAndInspectMultiWorktreeEngineerRun()
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout("ino-delegate-unit");
        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            Backend = WorkTreeBackendKind.ContainerVolume,
            VolumeMountRoot = Path.Combine(_root, "ino-delegate-volumes"),
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main", RemoteCloneUrl: bareOrigin));

        var coder = new CheckpointingRoslynCoder([]);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IWorkTree>(workTree);
        serviceCollection.AddSingleton<ICoder>(coder);
        serviceCollection.AddSingleton<IEngineerPlanner>(new TwoStepPlanner());
        serviceCollection.AddSingleton<IEngineerBuildVerifier>(new DeterministicBuildVerifier());
        serviceCollection.AddSingleton<ILiveVerifier>(new DeterministicLiveVerifier());
        serviceCollection.AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer());
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        var brain = new BrainId("owner");
        using var agentCaller = os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), brain));
        os.RegisterCatalog();

        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var runs = os.Get<IEngineerRuns>(IEngineerRuns.Key(brain.Value));
        var started = await runs.StartEngineerRun(
            new EngineerDelegationRequest(
                RepoSlug: "os",
                Goal: "Add Subtract and Modulo via Ino delegation",
                ParallelInstances: 2,
                Backend: "ContainerVolume",
                RunId: "inodeleg01"),
            Ct);

        Assert.True(started.Ok, started.SummaryText);
        Assert.Equal("owner/flow.engineer/inodeleg01", started.FlowId);

        EngineerRunSummary? waiting = null;
        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var check = await runs.InspectEngineerRun(started.FlowId, Ct);
            if (check.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed)
            {
                waiting = check;
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(waiting);
        Assert.Equal(FlowStatus.Waiting, waiting!.Status);
        Assert.Equal("approval-gate", waiting.CurrentStage);
        Assert.Equal(2, waiting.ParallelInstances);
        Assert.Equal("ContainerVolume", waiting.Backend);
        Assert.Equal(2, waiting.Instances.Length);
        Assert.NotNull(waiting.Card);
        Assert.Equal("approval", waiting.Card!.Kind);
        Assert.Contains("neuron://owner/apps/tasks#owner/flow.engineer/inodeleg01", waiting.EngineerAppUri, StringComparison.Ordinal);

        // IStudio.ChangeStatus also inspects flow.engineer runs seamlessly!
        var studio = os.Get<IStudio>(new NeuronId(brain, "ino.studio", "ino/studio"));
        var studioStatus = await studio.ChangeStatus("inodeleg01", Ct);
        Assert.True(studioStatus.Ok, studioStatus.Text);
        Assert.NotNull(studioStatus.Card);
        Assert.Equal("approval", studioStatus.Card!.Kind);

        // Clean up active worktrees via human cancel
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        await flows.Cancel(started.FlowId, "test cleanup", Ct);
        await os.Idle();
    }

    [Fact]
    public void EngineerCandidateScoreAndTournamentRankingSelectHighestPassingCleanCandidateAndTieBreakBySmallestDiff()
    {
        var shaFail = CommitSha.Parse(new string('1', 40));
        var shaLarge = CommitSha.Parse(new string('2', 40));
        var shaCleanSmall = CommitSha.Parse(new string('3', 40));

        var lease0 = new WorkTreeLease("run-t", "os", "/repo", "/wt/0", "/repo/.git/wt/0", new string('a', 64), "main", new string('0', 40), "ino/os-runt-w1", InstanceIndex: 0);
        var lease1 = new WorkTreeLease("run-t", "os", "/repo", "/wt/1", "/repo/.git/wt/1", new string('b', 64), "main", new string('0', 40), "ino/os-runt-w2", InstanceIndex: 1);
        var lease2 = new WorkTreeLease("run-t", "os", "/repo", "/wt/2", "/repo/.git/wt/2", new string('c', 64), "main", new string('0', 40), "ino/os-runt-w3", InstanceIndex: 2);

        var file = RepoPath.Parse("code/Calculator.cs");
        var diffTiny = "--- a/code/Calculator.cs\n+++ b/code/Calculator.cs\n@@ -1 +1,2 @@\n public static class Calculator {}\n+// broken\n";
        var diffLarge = string.Join('\n', ["--- a/code/Calculator.cs", "+++ b/code/Calculator.cs", "@@ -1 +1,18 @@", .. Enumerable.Range(1, 18).Select(static n => $"+public static int M{n}() => {n};")]);
        var diffSmall = "--- a/code/Calculator.cs\n+++ b/code/Calculator.cs\n@@ -1 +1,4 @@\n+public static int Sub(int a, int b) => a - b;\n+public static int Abs(int x) => x < 0 ? -x : x;\n+public static int Sign(int x) => Math.Sign(x);\n";

        var commit0 = ChangeSet.FromDiff(shaFail, [file], diffTiny);
        var commit1 = ChangeSet.FromDiff(shaLarge, [file], diffLarge);
        var commit2 = ChangeSet.FromDiff(shaCleanSmall, [file], diffSmall);

        var buildFail = new EngineerBuildReport(false, false, true, false, 0, 1, 0, 2, ["CS1002: ; expected"], 40, "Build failed");
        var buildPass = new EngineerBuildReport(true, true, true, true, 14, 0, 0, 0, [], 80, "Build & tests green");
        var livePass = new EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], [], "Live verify green");

        var c0 = new EngineerInstance(0, lease0, ["t1"], new CoderEdit("Attempt 0", "m", [file]), [], commit0, buildFail, null, "failed", "Build failed").WithComputedScore();
        var c1 = new EngineerInstance(1, lease1, ["t1"], new CoderEdit("Attempt 1", "m", [file]), [], commit1, buildPass, livePass, "verified").WithComputedScore();
        var c2 = new EngineerInstance(2, lease2, ["t1"], new CoderEdit("Attempt 2", "m", [file]), [], commit2, buildPass, livePass, "verified").WithComputedScore();

        Assert.False(c0.EffectiveScore.AllGatesPassed);
        Assert.True(c1.EffectiveScore.AllGatesPassed);
        Assert.True(c2.EffectiveScore.AllGatesPassed);
        Assert.True(c2.EffectiveScore.TotalScore > c1.EffectiveScore.TotalScore);
        Assert.True(c1.EffectiveScore.TotalScore > c0.EffectiveScore.TotalScore);

        var plan = new EngineerTaskPlan("Goal", "Arch", "Critic", [], [new("t1", "Task", "csharp", ["code/Calculator.cs"], [], "Do")]);
        var run = EngineerRun.Start("run-t", RepositoryId.Parse("os"), "Goal", instanceCount: 3)
            .WithPlan(plan)
            .WithLeases("run-t", [lease0, lease1, lease2])
            .WithUpdatedInstances([c0, c1, c2]);

        // Candidate 2 (w3) has highest passing score & smallest clean diff -> automatically selected!
        Assert.Equal(2, run.SelectedCandidateIndex);
        var ranked = run.RankCandidates();
        Assert.Equal(3, ranked.Length);
        Assert.Equal(3, ranked.Single(static r => r.InstanceIndex == 0).Rank);
        Assert.Equal(2, ranked.Single(static r => r.InstanceIndex == 1).Rank);
        Assert.Equal(1, ranked.Single(static r => r.InstanceIndex == 2).Rank);
        Assert.True(ranked.Single(static r => r.InstanceIndex == 2).IsSelected);

        // Promoting failed candidate 0 is refused; promoting passing candidate 1 succeeds!
        Assert.Throws<InvalidOperationException>(() => run.SelectCandidate(0));
        var promoted1 = run.SelectCandidate(1);
        Assert.Equal(1, promoted1.SelectedCandidateIndex);
        Assert.Equal(shaLarge, promoted1.SelectedInstance!.Commit!.Commit);
    }

    [Fact]
    public async Task EngineerFlowTournamentAutomaticallyPromotesPassingCandidateWhenSiblingFailsAndRebindsConfirmationShaOnSwitch()
    {
        // Part 1: Candidate w1 (0) fails build, Candidate w2 (1) passes -> flow automatically promotes w2!
        var (bare1, checkout1, home1) = CreateBareOriginAndCheckout("tournament-failover");
        var wt1 = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home1,
            VerifyFormatting = false,
        });
        wt1.RegisterRepository(new RegisteredRepository("os", checkout1, DefaultBaseBranch: "main"));

        var sp1 = new ServiceCollection()
            .AddSingleton<IWorkTree>(wt1)
            .AddSingleton<ICoder>(new TournamentVariantCoder())
            .AddSingleton<IEngineerPlanner>(new SingleStepPlanner())
            .AddSingleton<IEngineerBuildVerifier>(new FailFirstInstanceBuildVerifier())
            .AddSingleton<ILiveVerifier>(new DeterministicLiveVerifier())
            .AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer())
            .BuildServiceProvider();

        await using (var os1 = TestOS.Local(o => o.Services = sp1))
        {
            var brain = new BrainId("owner");
            using var person = os1.Edge.Enter(os1.Edge.Person("owner", "owner", brain));
            os1.RegisterCatalog();

            var flows1 = os1.Get<IFlows>(IFlows.Key(brain.Value));
            var runs1 = os1.Get<IEngineerRuns>(IEngineerRuns.Key(brain.Value));
            var started1 = await runs1.StartEngineerRun(
                new EngineerDelegationRequest("os", "Add Subtract variant", ParallelInstances: 2, Backend: "LocalGit", RunId: "tournfail1"),
                Ct);

            EngineerRunSummary? waiting1 = null;
            for (var i = 0; i < 60; i++)
            {
                await os1.Idle();
                var snap = await runs1.InspectEngineerRun(started1.FlowId, Ct);
                if (snap.Status is FlowStatus.Waiting or FlowStatus.Failed)
                {
                    waiting1 = snap;
                    break;
                }

                await Task.Delay(40, Ct);
            }

            Assert.NotNull(waiting1);
            Assert.Equal(FlowStatus.Waiting, waiting1!.Status);
            Assert.Equal(1, waiting1.SelectedCandidateIndex);
            var winnerW2Sha = waiting1.Instances.Single(static i => i.InstanceIndex == 1).CommitSha!;
            Assert.Equal(winnerW2Sha, waiting1.WinningCommitSha);
            Assert.EndsWith(":" + winnerW2Sha, waiting1.ConfirmationPlanId!, StringComparison.Ordinal);

            // Attempting to switch to failed candidate w1 (0) at approval-gate is refused!
            await Assert.ThrowsAsync<InvalidOperationException>(() => runs1.SelectEngineerCandidate(started1.FlowId, 0, Ct));

            await flows1.Cancel(started1.FlowId, "cleanup", Ct);
            await os1.Idle();
        }

        await sp1.DisposeAsync();

        // Part 2: Both w1 (0) and w2 (1) pass; human switches w1 -> w2 -> w1 -> w2 at approval-gate and publishes w2!
        var (bare2, checkout2, home2) = CreateBareOriginAndCheckout("tournament-switch");
        var wt2 = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home2,
            VerifyFormatting = false,
        });
        wt2.RegisterRepository(new RegisteredRepository("os", checkout2, DefaultBaseBranch: "main"));

        await using var sp2 = new ServiceCollection()
            .AddSingleton<IWorkTree>(wt2)
            .AddSingleton<ICoder>(new TournamentVariantCoder())
            .AddSingleton<IEngineerPlanner>(new SingleStepPlanner())
            .AddSingleton<IEngineerBuildVerifier>(new DeterministicBuildVerifier())
            .AddSingleton<ILiveVerifier>(new DeterministicLiveVerifier())
            .AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer())
            .BuildServiceProvider();

        await using var os2 = TestOS.Local(o => o.Services = sp2);
        var brain2 = new BrainId("owner");
        using var owner2 = os2.Edge.Enter(os2.Edge.Person("owner", "owner", brain2));
        os2.RegisterCatalog();

        var flows2 = os2.Get<IFlows>(IFlows.Key(brain2.Value));
        var runs2 = os2.Get<IEngineerRuns>(IEngineerRuns.Key(brain2.Value));
        var started2 = await runs2.StartEngineerRun(
            new EngineerDelegationRequest("os", "Add Subtract tournament variants", ParallelInstances: 2, Backend: "LocalGit", RunId: "tournswitch1"),
            Ct);

        EngineerRunSummary? waiting2 = null;
        for (var i = 0; i < 60; i++)
        {
            await os2.Idle();
            var snap = await runs2.InspectEngineerRun(started2.FlowId, Ct);
            if (snap.Status is FlowStatus.Waiting or FlowStatus.Failed)
            {
                waiting2 = snap;
                break;
            }

            await Task.Delay(40, Ct);
        }

        Assert.NotNull(waiting2);
        Assert.Equal(FlowStatus.Waiting, waiting2!.Status);
        Assert.Equal(0, waiting2.SelectedCandidateIndex);

        var sha0 = waiting2.Instances.Single(static i => i.InstanceIndex == 0).CommitSha!;
        var sha1 = waiting2.Instances.Single(static i => i.InstanceIndex == 1).CommitSha!;
        Assert.NotEqual(sha0, sha1);
        Assert.EndsWith(":" + sha0, waiting2.ConfirmationPlanId!, StringComparison.Ordinal);

        // Switch 0 -> 1 -> 0 -> 1 while parked at approval-gate: each switch withdraws the prior plan and mints a fresh SHA-bound plan!
        var switchedTo1 = await runs2.SelectEngineerCandidate(started2.FlowId, 1, Ct);
        await os2.Idle();
        Assert.Equal(1, switchedTo1.SelectedCandidateIndex);
        Assert.Equal(sha1, switchedTo1.WinningCommitSha);
        Assert.EndsWith(":" + sha1, switchedTo1.ConfirmationPlanId!, StringComparison.Ordinal);
        Assert.NotEqual(waiting2.ConfirmationPlanId, switchedTo1.ConfirmationPlanId);

        var switchedBackTo0 = await runs2.SelectEngineerCandidate(started2.FlowId, 0, Ct);
        await os2.Idle();
        Assert.Equal(0, switchedBackTo0.SelectedCandidateIndex);
        Assert.Equal(sha0, switchedBackTo0.WinningCommitSha);
        Assert.EndsWith(":" + sha0, switchedBackTo0.ConfirmationPlanId!, StringComparison.Ordinal);

        var finalPromoted1 = await runs2.SelectEngineerCandidate(started2.FlowId, 1, Ct);
        await os2.Idle();
        Assert.Equal(1, finalPromoted1.SelectedCandidateIndex);
        Assert.Equal(sha1, finalPromoted1.WinningCommitSha);
        Assert.EndsWith(":" + sha1, finalPromoted1.ConfirmationPlanId!, StringComparison.Ordinal);

        var inbox = os2.Get<IConfirmationInbox>(new NeuronId(brain2, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var pendingOnly = Assert.Single((await inbox.Read()).Pending);
        Assert.Equal(finalPromoted1.ConfirmationPlanId, pendingOnly.PlanId);
        Assert.Equal(sha1, pendingOnly.Plan.Subject);

        await inbox.Resolve(finalPromoted1.ConfirmationPlanId!, approve: true);
        EngineerRunSummary? done2 = null;
        for (var i = 0; i < 60; i++)
        {
            await os2.Idle();
            var snap = await runs2.InspectEngineerRun(started2.FlowId, Ct);
            if (snap.Status is FlowStatus.Completed or FlowStatus.Failed)
            {
                done2 = snap;
                break;
            }

            await Task.Delay(40, Ct);
        }

        Assert.NotNull(done2);
        Assert.Equal(FlowStatus.Completed, done2!.Status);
        Assert.Equal(sha1, done2.WinningCommitSha);
        Assert.Equal(sha1, RunGit(bare2, ["rev-parse", done2.PushedRef!]).Trim());
    }

    [Fact]
    public async Task NoOverlappingFileSlicesRuleRefusesParallelOverlapAndAllowsSequentialWaveDependencies()
    {
        var overlappingSameWave = new[]
        {
            new EngineerTaskSlice("s1", "Edit Calculator A", ["code/Calculator.cs"], [], AssignedAgentKey: "ino.slice-1"),
            new EngineerTaskSlice("s2", "Edit Calculator B", ["code/Calculator.cs"], [], AssignedAgentKey: "ino.slice-2"),
        };

        var rule = new NoOverlappingFileSlices(overlappingSameWave);
        Assert.Equal("overlapping_file_slices", rule.Name);
        Assert.True(rule.Broken);
        Assert.Contains("s1", rule.Message, StringComparison.Ordinal);
        Assert.Contains("s2", rule.Message, StringComparison.Ordinal);

        // Directory prefix overlap in same wave is also caught!
        var prefixOverlap = new[]
        {
            new EngineerTaskSlice("s-dir", "Own directory", ["code/math"], []),
            new EngineerTaskSlice("s-file", "Own nested file", ["code/math/Adder.cs"], []),
        };
        Assert.True(new NoOverlappingFileSlices(prefixOverlap).Broken);

        // Sequential dependency across Wave 0 -> Wave 1 on the same file is valid!
        var sequentialWaves = new[]
        {
            new EngineerTaskSlice("w0", "Foundation", ["code/Calculator.cs"], []),
            new EngineerTaskSlice("w1", "Refinement", ["code/Calculator.cs"], ["w0"]),
        };
        var seqRule = new NoOverlappingFileSlices(sequentialWaves);
        Assert.False(seqRule.Broken);
        var waves = EngineerTaskSlices.ComputeWaves(sequentialWaves);
        Assert.Equal(2, waves.Length);

        // InoNativeHarness refuses overlapping parallel slices with RuleViolationException ('overlapping_file_slices')!
        var wtDir = Path.Combine(_root, "overlap-refuse-wt");
        Directory.CreateDirectory(wtDir);
        var harness = InoNativeHarness.CreateDefault();
        var badRequest = new EngineeringHarnessRequest(
            FlowId: "owner/flow.engineer/overlap1",
            RunId: "overlap1",
            RepositoryId: "os",
            Goal: "Attempt overlapping parallel slices",
            WorktreeId: "wt-0",
            WorktreePath: wtDir,
            InstanceIndex: 0,
            TotalInstances: 1,
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            StrategyPromptLens: "Minimal diff.",
            Harness: EngineeringHarnessKind.InoNative,
            RatifiedPlan: new RatifiedPlan("Overlap", "Arch", [], overlappingSameWave));

        var ex = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(() => harness.ExecuteAsync(badRequest, ct: Ct));
        Assert.Contains("overlapping_file_slices", ex.Rejected.Rules);
    }

    [Fact]
    public async Task InoNativeHarnessExecutesThreeDisjointSlicesInParallelDeletesCleanupTargetsStreamsProgressAndWritesSessionJournal()
    {
        var wtDir = Path.Combine(_root, "ino-native-3slice-wt");
        Directory.CreateDirectory(Path.Combine(wtDir, "code"));
        await File.WriteAllTextAsync(Path.Combine(wtDir, ".git"), "gitdir: C:/fake/wt\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(wtDir, "code", "LegacyTrash.cs"), "namespace Demo; public static class LegacyTrash {}\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(wtDir, "code", "Existing.cs"), """
            namespace Demo;

            public static class Existing
            {
                public static int KeepMe() => 1;
                public static int DeadMethod() => 999;
            }
            """, Ct);

        var activeConcurrentSlices = 0;
        var peakConcurrentSlices = 0;
        var allThreeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hydratedPrompts = new List<string>();
        var roslyn = new WorktreeRoslynAnalyzer();

        var parallelCoder = new BarrierParallelSliceCoder(
            roslyn,
            expectedParticipants: 3,
            onEnter: prompt =>
            {
                lock (hydratedPrompts)
                {
                    hydratedPrompts.Add(prompt);
                }

                var nowActive = Interlocked.Increment(ref activeConcurrentSlices);
                int initial;
                do
                {
                    initial = Volatile.Read(ref peakConcurrentSlices);
                    if (nowActive <= initial) { break; }
                }
                while (Interlocked.CompareExchange(ref peakConcurrentSlices, nowActive, initial) != initial);

                if (nowActive >= 3)
                {
                    allThreeEntered.TrySetResult();
                }
            },
            onExit: () => Interlocked.Decrement(ref activeConcurrentSlices),
            barrier: allThreeEntered.Task);

        var fakeMemory = new FakeWorkingMemory();
        var fakeJournal = new FakeSessionJournal();
        var progressSignals = new List<OperationProgressed>();

        var harness = InoNativeHarness.CreateDefault(
            coder: parallelCoder,
            roslyn: roslyn,
            workingMemory: fakeMemory,
            sessionJournal: fakeJournal);

        EngineerTaskSlice[] threeDisjointSlices =
        [
            new("slice-add", "Create Adder helper", ["code/Adder.cs"], [], AssignedAgentKey: "ino.slice-1", Language: "csharp", Instructions: "Create Adder"),
            new("slice-mul", "Create Multiplier helper", ["code/Multiplier.cs"], [], AssignedAgentKey: "ino.slice-2", Language: "csharp", Instructions: "Create Multiplier"),
            new("slice-clamp", "Create Clamper helper", ["code/Clamper.cs"], [], AssignedAgentKey: "ino.slice-3", Language: "csharp", Instructions: "Create Clamper"),
        ];

        var ratified = new RatifiedPlan(
            Goal: "Clean up legacy trash and create three math helpers concurrently",
            ArchitectureSummary: "3 parallel Wave-0 AgentNeuron slices over disjoint files",
            CleanupTargets: ["code/LegacyTrash.cs", "code/Existing.cs#Existing.DeadMethod"],
            Slices: threeDisjointSlices);

        var request = new EngineeringHarnessRequest(
            FlowId: "owner/flow.engineer/parallel3",
            RunId: "parallel3",
            RepositoryId: "os",
            Goal: ratified.Goal,
            WorktreeId: "wt-0",
            WorktreePath: wtDir,
            InstanceIndex: 0,
            TotalInstances: 1,
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            StrategyPromptLens: "Minimal diff.",
            Harness: EngineeringHarnessKind.InoNative,
            RatifiedPlan: ratified,
            Brain: new BrainId("owner"));

        var result = await harness.ExecuteAsync(
            request,
            onProgress: sig =>
            {
                lock (progressSignals)
                {
                    progressSignals.Add(sig);
                }

                return Task.CompletedTask;
            },
            ct: Ct);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(3, peakConcurrentSlices);
        Assert.Equal(1, fakeMemory.StartupPackReadCount);
        Assert.Equal(1, fakeMemory.BriefReadCount);
        Assert.All(hydratedPrompts, static p => Assert.Contains("BRIEF[os-conventions]: Prefer pure static methods", p, StringComparison.Ordinal));

        // CleanupTargets verified: LegacyTrash.cs deleted on disk AND Existing.DeadMethod removed via AST!
        Assert.False(File.Exists(Path.Combine(wtDir, "code", "LegacyTrash.cs")));
        Assert.Contains("code/LegacyTrash.cs", result.DeletedFiles);
        var existingUpdated = await File.ReadAllTextAsync(Path.Combine(wtDir, "code", "Existing.cs"), Ct);
        Assert.Contains("KeepMe", existingUpdated, StringComparison.Ordinal);
        Assert.DoesNotContain("DeadMethod", existingUpdated, StringComparison.Ordinal);

        // All 3 disjoint slice files created and verified with 0 Roslyn semantic errors!
        Assert.True(File.Exists(Path.Combine(wtDir, "code", "Adder.cs")));
        Assert.True(File.Exists(Path.Combine(wtDir, "code", "Multiplier.cs")));
        Assert.True(File.Exists(Path.Combine(wtDir, "code", "Clamper.cs")));
        Assert.Equal(0, result.SemanticErrorCount);
        Assert.Equal(3, result.CompletedSlices.Length);
        Assert.All(result.CompletedSlices, static s =>
        {
            Assert.Equal("completed", s.Status);
            Assert.Equal(100, s.Percent);
        });

        // Verify 0% -> 25% -> 60% -> 90% -> 100% OperationProgressed signal stream for each of the 3 slices!
        foreach (var sliceId in new[] { "slice-add", "slice-mul", "slice-clamp" })
        {
            var percents = progressSignals
                .Where(p => p.SliceId == sliceId)
                .Select(static p => p.Percent)
                .ToArray();
            Assert.Equal([0, 25, 60, 90, 100], percents);
        }

        // Verify post-mission SessionMilestone writeback to ISessionJournal!
        var milestone = Assert.Single(fakeJournal.Milestones);
        Assert.Equal(ratified.Goal, milestone.Goal);
        Assert.Equal("os-conventions", milestone.ProjectBrief);
        Assert.Contains(milestone.Deliverables!, static d => d.Contains("slice-add", StringComparison.Ordinal));
        Assert.Contains(milestone.Decisions!, static d => d.Contains("Deleted 1 dead cleanup target", StringComparison.Ordinal));
        Assert.NotEmpty(milestone.OpenThreads!);
    }

    [Fact]
    public async Task RatifyEngineerPlanPausesAtBrainstormRefusesOverlappingSlicesAndResumesPipelineOnDisjointApproval()
    {
        var (bareOrigin, checkout, home) = CreateBareOriginAndCheckout("ratify-lifecycle");
        await File.WriteAllTextAsync(Path.Combine(checkout, "code", "DeadHelper.cs"), "namespace Demo; public static class DeadHelper {}\n", Ct);
        RunGit(checkout, ["add", "."]);
        RunGit(checkout, ["commit", "-m", "Seed DeadHelper for cleanup"]);
        RunGit(checkout, ["push", "origin", "main"]);

        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VerifyFormatting = false,
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));

        await using var sp = new ServiceCollection()
            .AddSingleton<IWorkTree>(workTree)
            .AddSingleton<ICoder>(new SingleFileRoslynCoder())
            .AddSingleton<IEngineerPlanner>(new SingleStepPlanner())
            .AddSingleton<IEngineerBuildVerifier>(new DeterministicBuildVerifier())
            .AddSingleton<ILiveVerifier>(new DeterministicLiveVerifier())
            .AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer())
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = sp);
        var brain = new BrainId("owner");
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        os.RegisterCatalog();

        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var runs = os.Get<IEngineerRuns>(IEngineerRuns.Key(brain.Value));
        var started = await runs.StartEngineerRun(
            new EngineerDelegationRequest(
                RepoSlug: "os",
                Goal: "Delete DeadHelper and add Multiply to Calculator",
                ParallelInstances: 1,
                Backend: "LocalGit",
                RunId: "ratify01",
                RequirePlanRatification: true,
                Harness: EngineeringHarnessKind.InoNative,
                CleanupTargets: ["code/DeadHelper.cs"]),
            Ct);

        EngineerRunSummary? pausedAtPlan = null;
        for (var i = 0; i < 40; i++)
        {
            await os.Idle();
            var snap = await runs.InspectEngineerRun(started.FlowId, Ct);
            if (snap.AwaitingPlanRatification)
            {
                pausedAtPlan = snap;
                break;
            }

            await Task.Delay(30, Ct);
        }

        Assert.NotNull(pausedAtPlan);
        Assert.Equal(FlowStatus.Waiting, pausedAtPlan!.Status);
        Assert.Equal("brainstorm-and-plan", pausedAtPlan.CurrentStage);
        Assert.True(pausedAtPlan.AwaitingPlanRatification);
        Assert.NotNull(pausedAtPlan.Collaborators);
        Assert.Contains(pausedAtPlan.Collaborators!, static c => c.Kind == "person" && c.FocusNodeId == "brainstorm-and-plan");
        Assert.Contains(pausedAtPlan.Collaborators!, static c => c.Kind == "ino-agent");

        // Attempting to ratify overlapping parallel slices is refused by NoOverlappingFileSlices!
        var badSlices = new[]
        {
            new EngineerTaskSlice("s1", "A", ["code/Calculator.cs"], []),
            new EngineerTaskSlice("s2", "B", ["code/Calculator.cs"], []),
        };
        var overlapEx = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(
            () => runs.RatifyEngineerPlan(started.FlowId, ["code/DeadHelper.cs"], badSlices, Ct));
        Assert.Contains("overlapping_file_slices", overlapEx.Rejected.Rules);

        // Ratify with clean disjoint slice + approved CleanupTargets -> advances to approval-gate!
        var validSlices = new[]
        {
            new EngineerTaskSlice("s1-mul", "Add Multiply", ["code/Calculator.cs"], [], AssignedAgentKey: "ino.slice-1"),
        };
        _ = await runs.RatifyEngineerPlan(started.FlowId, ["code/DeadHelper.cs"], validSlices, Ct);

        EngineerRunSummary? atApprovalGate = null;
        for (var i = 0; i < 60; i++)
        {
            await os.Idle();
            var snap = await runs.InspectEngineerRun(started.FlowId, Ct);
            if (snap.Status == FlowStatus.Waiting && snap.CurrentStage == "approval-gate")
            {
                atApprovalGate = snap;
                break;
            }

            await Task.Delay(40, Ct);
        }

        Assert.NotNull(atApprovalGate);
        Assert.False(atApprovalGate!.AwaitingPlanRatification);
        Assert.True(atApprovalGate.RatifiedPlan?.Ratified);
        Assert.Equal(["code/DeadHelper.cs"], atApprovalGate.Instances[0].DeletedFiles!);
        Assert.Contains("code/DeadHelper.cs", atApprovalGate.Instances[0].ChangedFiles);
        Assert.Contains("code/Calculator.cs", atApprovalGate.Instances[0].ChangedFiles);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
        await os.Idle();
    }

    [Fact]
    public async Task ExternalCliHarnessMapsClaudeAndCodexArgumentsSyncsWorktreeIncludeAndStreamsProgress()
    {
        var sourceRepo = Path.Combine(_root, "cli-src-repo");
        var wtDir = Path.Combine(_root, "cli-wt-dir");
        Directory.CreateDirectory(Path.Combine(sourceRepo, "config"));
        Directory.CreateDirectory(Path.Combine(wtDir, "code"));

        await File.WriteAllTextAsync(Path.Combine(sourceRepo, ".worktreeinclude"), "# copy local dev config\nconfig/local.settings.txt\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(sourceRepo, "config", "local.settings.txt"), "MODE=test\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(wtDir, "code", "Calculator.cs"), "namespace Demo;\npublic static class Calculator { public static int Add(int a, int b) => a + b; }\n", Ct);

        var captured = new List<SandboxCommandRequest>();
        var fakeCliSandbox = new MutatingCliSandboxRunner(captured);

        var claude = new ExternalCliHarness(EngineeringHarnessKind.ClaudeCli, sandboxRunner: fakeCliSandbox);
        var codex = new ExternalCliHarness(EngineeringHarnessKind.CodexCli, sandboxRunner: fakeCliSandbox);

        Assert.Equal(["-p", "Fix bug", "--output-format", "stream-json", "--verbose"], claude.BuildCliArguments("Fix bug"));
        Assert.Equal(["exec", "--json", "Fix bug"], codex.BuildCliArguments("Fix bug"));
        _ = await claude.IsAvailableAsync(Ct);

        var claudeProgress = new List<OperationProgressed>();
        var req = new EngineeringHarnessRequest(
            FlowId: "owner/flow.engineer/cli1",
            RunId: "cli1",
            RepositoryId: "os",
            Goal: "Add Subtract via CLI benchmark",
            WorktreeId: "wt-0",
            WorktreePath: wtDir,
            InstanceIndex: 0,
            TotalInstances: 1,
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            StrategyPromptLens: "Minimal diff.",
            Harness: EngineeringHarnessKind.ClaudeCli,
            RatifiedPlan: new RatifiedPlan("Add Subtract via CLI benchmark", "CLI adapter", [], []),
            SourceRepositoryPath: sourceRepo);

        var res = await claude.ExecuteAsync(req, sig =>
        {
            claudeProgress.Add(sig);
            return Task.CompletedTask;
        }, Ct);

        Assert.True(res.Succeeded, res.FailureDetail);
        Assert.True(File.Exists(Path.Combine(wtDir, "config", "local.settings.txt")));
        Assert.Contains("code/Calculator.cs", res.TouchedFiles);
        Assert.Equal("Applied Subtract via stream-json.", res.Summary);
        Assert.Equal([0, 25, 60, 90, 100], claudeProgress.Select(static p => p.Percent).ToArray());
        Assert.Single(captured, static c => c.Executable == "claude" && c.Arguments.Contains("stream-json", StringComparer.Ordinal));
    }

    [Fact]
    public async Task EngineerPlannerHydratesRoslynCodeGraphAndWorkingMemoryBriefIntoDisjointRatifiedPlanSlices()
    {
        var (_, checkout, home) = CreateBareOriginAndCheckout("planner-codegraph");
        await File.WriteAllTextAsync(
            Path.Combine(checkout, "code", "Vector2Int.cs"),
            """
            namespace Demo;

            public readonly record struct Vector2Int(int X, int Y);
            """,
            Ct);
        await File.WriteAllTextAsync(
            Path.Combine(checkout, "code", "LegacyObsoleteMath.cs"),
            """
            namespace Demo;

            public static class LegacyObsoleteMath
            {
                public static int Obsolete() => -1;
            }
            """,
            Ct);
        await File.WriteAllTextAsync(
            Path.Combine(checkout, "code", "DuplicateCalculatorMirror.cs"),
            """
            namespace DemoMirror;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """,
            Ct);

        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VerifyFormatting = false,
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));

        var roslyn = new WorktreeRoslynAnalyzer();
        var repoBriefMemory = new RepoBriefWorkingMemory(
            "repo:os",
            "Delete LegacyObsoleteMath.cs and keep Calculator and Vector2Int pure value helpers.");

        // Part 1: Deterministic synthesis (without IModelInference) embeds <= 2 KB CodeGraphDigest, grounded CleanupTargets, and disjoint Wave-0 slices!
        await using var deterministicSp = new ServiceCollection()
            .AddSingleton<IWorkTree>(workTree)
            .AddSingleton<IWorktreeRoslyn>(roslyn)
            .AddSingleton<IWorkingMemory>(repoBriefMemory)
            .BuildServiceProvider();

        var deterministicPlanner = new EngineerPlanner(deterministicSp);
        var deterministicPlan = await deterministicPlanner.PlanAsync(
            "os",
            "Delete LegacyObsoleteMath and add Subtract to Calculator plus Dot to Vector2Int",
            [".cs"],
            maxChangedFiles: 5,
            Ct);

        Assert.Null(deterministicPlan.Validate(5));
        Assert.False(string.IsNullOrWhiteSpace(deterministicPlan.CodeGraphDigest));
        Assert.True(Encoding.UTF8.GetByteCount(deterministicPlan.CodeGraphDigest!) <= RepositoryCodeGraphDigest.MaxBytes);
        Assert.Contains("BRIEF[repo:os]: Delete LegacyObsoleteMath.cs", deterministicPlan.CodeGraphDigest!, StringComparison.Ordinal);
        Assert.Contains("CLEANUP_CANDIDATES:", deterministicPlan.CodeGraphDigest!, StringComparison.Ordinal);
        Assert.Contains("DUPLICATE_SYMBOLS: Calculator", deterministicPlan.CodeGraphDigest!, StringComparison.Ordinal);
        Assert.Contains("code/Calculator.cs", deterministicPlan.CodeGraphDigest!, StringComparison.Ordinal);
        Assert.Contains("code/Vector2Int.cs", deterministicPlan.CodeGraphDigest!, StringComparison.Ordinal);

        var ratified = deterministicPlan.ToRatifiedPlan();
        Assert.Contains("code/LegacyObsoleteMath.cs", ratified.CleanupTargets);
        Assert.False(ratified.DisjointSlicesRule().Broken, ratified.DisjointSlicesRule().Message);
        Assert.Contains(ratified.Slices, static s => s.OwnedPaths.Contains("code/Calculator.cs", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(ratified.Slices, static s => s.OwnedPaths.Contains("code/Vector2Int.cs", StringComparer.OrdinalIgnoreCase));
        Assert.Single(ratified.TopologicalWaves());
        Assert.True(ratified.TopologicalWaves()[0].Length >= 2);

        // Part 2: Multi-agent Architect + Critic prompts receive the compact <= 2 KB RepositoryCodeGraphDigest and normalize overlapping slices!
        var seenPrompts = new List<string>();
        var turns = new Queue<Func<InferenceRequest, AiMessage>>(
        [
            req =>
            {
                var prompt = string.Join("\n", req.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text));
                seenPrompts.Add(prompt);
                return new AiMessage("assistant",
                [
                    new AiText("""
                        {
                          "goal": "Delete LegacyObsoleteMath and update Calculator & Vector2Int",
                          "architectureSummary": "Grounded CodeGraph plan across Calculator.cs and Vector2Int.cs.",
                          "cleanupTargets": ["code/LegacyObsoleteMath.cs"],
                          "tasks": [
                            { "id": "s-calc", "title": "Add Subtract", "language": "csharp", "targetFiles": ["code/Calculator.cs"], "dependsOn": [], "instructions": "Add Subtract" },
                            { "id": "s-vec", "title": "Add Dot", "language": "csharp", "targetFiles": ["code/Vector2Int.cs"], "dependsOn": [], "instructions": "Add Dot" }
                          ]
                        }
                        """),
                ]);
            },
            req =>
            {
                var prompt = string.Join("\n", req.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text));
                seenPrompts.Add(prompt);
                return new AiMessage("assistant",
                [
                    new AiText("""
                        {
                          "goal": "Delete LegacyObsoleteMath and update Calculator & Vector2Int",
                          "architectureSummary": "Grounded CodeGraph plan with disjoint Wave-0 slices.",
                          "criticReview": "Verified NoOverlappingFileSlices and LegacyObsoleteMath cleanup.",
                          "cleanupTargets": ["code/LegacyObsoleteMath.cs"],
                          "tasks": [
                            { "id": "s-calc", "title": "Add Subtract", "language": "csharp", "targetFiles": ["code/Calculator.cs"], "dependsOn": [], "instructions": "Add Subtract" },
                            { "id": "s-vec", "title": "Add Dot", "language": "csharp", "targetFiles": ["code/Vector2Int.cs"], "dependsOn": [], "instructions": "Add Dot" }
                          ],
                          "slices": [
                            { "sliceId": "s-calc", "title": "Add Subtract", "ownedPaths": ["code/Calculator.cs"], "dependsOnSliceIds": [], "assignedAgentKey": "ino.slice-1", "language": "csharp", "instructions": "Add Subtract" },
                            { "sliceId": "s-vec", "title": "Add Dot", "ownedPaths": ["code/Vector2Int.cs"], "dependsOnSliceIds": [], "assignedAgentKey": "ino.slice-2", "language": "csharp", "instructions": "Add Dot" }
                          ]
                        }
                        """),
                ]);
            },
        ]);

        await using var llmSp = new ServiceCollection()
            .AddSingleton<IWorkTree>(workTree)
            .AddSingleton<IWorktreeRoslyn>(roslyn)
            .AddSingleton<IWorkingMemory>(repoBriefMemory)
            .AddSingleton<IModelInference>(new QueueInference(turns))
            .BuildServiceProvider();

        var llmPlanner = new EngineerPlanner(llmSp);
        var llmPlan = await llmPlanner.PlanAsync("os", "Delete LegacyObsoleteMath and update Calculator & Vector2Int", [".cs"], 5, Ct);

        Assert.Equal(2, seenPrompts.Count);
        Assert.All(seenPrompts, static p =>
        {
            Assert.Contains("[Repository CodeGraphDigest (<=2KB)]", p, StringComparison.Ordinal);
            Assert.Contains("BRIEF[repo:os]:", p, StringComparison.Ordinal);
            Assert.Contains("CODEGRAPH_OUTLINES:", p, StringComparison.Ordinal);
        });

        var llmRatified = llmPlan.ToRatifiedPlan();
        Assert.Equal(["code/LegacyObsoleteMath.cs"], llmRatified.CleanupTargets);
        Assert.False(llmRatified.DisjointSlicesRule().Broken);
        Assert.Equal(2, llmRatified.Slices.Length);
    }

    [Fact]
    public async Task InoNativeHarnessCatchesTurnOneCSharpDiagnosticRepairsViaBoundedSelfRepairAndEnforcesTwoTurnCap()
    {
        var wtDir = Path.Combine(_root, "self-repair-wt");
        Directory.CreateDirectory(Path.Combine(wtDir, "code"));
        await File.WriteAllTextAsync(Path.Combine(wtDir, ".git"), "gitdir: C:/fake/wt\n", Ct);
        await File.WriteAllTextAsync(
            Path.Combine(wtDir, "code", "Calculator.cs"),
            """
            namespace Demo;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """,
            Ct);

        var roslyn = new WorktreeRoslynAnalyzer();
        var observedPrompts = new List<string>();
        var repairingCoder = new TurnOneErrorTurnTwoRepairCoder(observedPrompts);
        var progressEvents = new List<OperationProgressed>();

        var harness = InoNativeHarness.CreateDefault(coder: repairingCoder, roslyn: roslyn);
        var slice = new EngineerTaskSlice(
            SliceId: "slice-sub",
            Title: "Add Subtract to Calculator",
            OwnedPaths: ["code/Calculator.cs"],
            DependsOnSliceIds: [],
            AssignedAgentKey: "ino.slice-sub",
            Language: "csharp",
            Instructions: "Add Subtract(int a, int b)");
        var request = new EngineeringHarnessRequest(
            FlowId: "owner/flow.engineer/repair1",
            RunId: "repair1",
            RepositoryId: "os",
            Goal: "Add Subtract to Calculator",
            WorktreeId: "wt-0",
            WorktreePath: wtDir,
            InstanceIndex: 0,
            TotalInstances: 1,
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            StrategyPromptLens: "Minimal diff.",
            Harness: EngineeringHarnessKind.InoNative,
            RatifiedPlan: new RatifiedPlan("Add Subtract to Calculator", "Single slice", [], [slice]));

        var result = await harness.ExecuteAsync(
            request,
            onProgress: sig =>
            {
                lock (progressEvents)
                {
                    progressEvents.Add(sig);
                }

                return Task.CompletedTask;
            },
            ct: Ct);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(0, result.SemanticErrorCount);
        Assert.Equal(1, result.SelfRepairAttempts);
        var completedSlice = Assert.Single(result.CompletedSlices);
        Assert.Equal("completed", completedSlice.Status);
        Assert.Equal(1, completedSlice.SelfRepairAttempts);

        // Turn 1 injected CS0029; Turn 2 received compiler diagnostic + line snippet and repaired it!
        Assert.Equal(2, observedPrompts.Count);
        Assert.Contains("[Intra-Slice Roslyn Self-Repair — Attempt 1/2]", observedPrompts[1], StringComparison.Ordinal);
        Assert.Contains("CS0029", observedPrompts[1], StringComparison.Ordinal);
        Assert.Contains("not_an_int", observedPrompts[1], StringComparison.Ordinal);

        var repairSignal = Assert.Single(progressEvents, static p => p.Phase == "self-repair");
        Assert.Equal("Repairing CS0029 in code/Calculator.cs (attempt 1/2)", repairSignal.Detail);

        var finalSource = await File.ReadAllTextAsync(Path.Combine(wtDir, "code", "Calculator.cs"), Ct);
        Assert.Contains("public static int Subtract(int a, int b) => a - b;", finalSource, StringComparison.Ordinal);

        // Part 2: Persistent syntax error stops strictly at MaxSelfRepairTurns == 2 (1 initial + 2 repair turns = 3 invocations)
        var brokenWtDir = Path.Combine(_root, "self-repair-cap-wt");
        Directory.CreateDirectory(Path.Combine(brokenWtDir, "code"));
        await File.WriteAllTextAsync(Path.Combine(brokenWtDir, ".git"), "gitdir: C:/fake/wt\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(brokenWtDir, "code", "Calculator.cs"), "namespace Demo;\npublic static class Calculator {}\n", Ct);

        var capCoder = new AlwaysSyntaxBrokenCoder();
        var capProgress = new List<OperationProgressed>();
        var capHarness = InoNativeHarness.CreateDefault(coder: capCoder, roslyn: roslyn);
        var capResult = await capHarness.ExecuteAsync(
            request with { WorktreePath = brokenWtDir, RunId = "repaircap" },
            onProgress: sig =>
            {
                lock (capProgress)
                {
                    capProgress.Add(sig);
                }

                return Task.CompletedTask;
            },
            ct: Ct);

        Assert.False(capResult.Succeeded);
        Assert.Equal(1 + InoNativeHarness.MaxSelfRepairTurns, capCoder.Invocations);
        Assert.Equal(InoNativeHarness.MaxSelfRepairTurns, capResult.SelfRepairAttempts);
        Assert.Equal(InoNativeHarness.MaxSelfRepairTurns, Assert.Single(capResult.CompletedSlices).SelfRepairAttempts);
        Assert.Equal(
            [
                "Repairing CS1002 in code/Calculator.cs (attempt 1/2)",
                "Repairing CS1002 in code/Calculator.cs (attempt 2/2)",
            ],
            capProgress.Where(static p => p.Phase == "self-repair").Select(static p => p.Detail).ToArray());
    }

    [Fact]
    public async Task EngineerLiveVerifierValidatesLiveAggregateScreenViewsPlaywrightProbesAndCatchesBrokenUiState()
    {
        var treeDir = Path.Combine(_root, "live-ui-gate-wt");
        Directory.CreateDirectory(treeDir);
        var srcRoot = "s" + "rc";
        string[] uiTouchedFiles =
        [
            $"{srcRoot}/IntoChat.OS.Apps/Tasks/TasksApp.cs",
            "ui/lib/desktop/live_desktop_shell.dart",
        ];

        Assert.True(EngineerLiveVerifier.TouchesUiSurface(uiTouchedFiles));
        Assert.False(EngineerLiveVerifier.TouchesUiSurface([$"{srcRoot}/Modules/Catalog/Flows/EngineerFlow.cs"]));

        // Host the real TasksApp AggregateScreen on TestOS to verify valid live View tree + AppShellNode + AutomationName invariants!
        await using var os = TestOS.Local();
        var brain = new BrainId("owner");
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        os.RegisterCatalog();
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(IntoChat.OS.Apps.Tasks.TasksApp).Assembly);

        var liveTasksScreen = os.Get<IScreen>(new NeuronId(brain, "app.tasks", "default"));
        var openedTasksView = await liveTasksScreen.Open();
        Assert.NotNull(openedTasksView);

        var healthyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Engineer:LiveBaseUrl"] = "http://127.0.0.1:19876",
            })
            .Build();
        var healthyHttpFactory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"Healthy\"}", Encoding.UTF8, "application/json"),
        });
        var healthyPageSession = new FakeBrowserPageSession(
            connected: true,
            new BrowserObservation("http://127.0.0.1:19876/tasks", "IntoChat Tasks", "Tasks shell ready", []));

        await using var healthySp = new ServiceCollection()
            .AddSingleton<IConfiguration>(healthyConfig)
            .AddSingleton<IHttpClientFactory>(healthyHttpFactory)
            .AddSingleton<IBrowserPageSession>(healthyPageSession)
            .BuildServiceProvider();

        using var healthyVerifier = new EngineerLiveVerifier(services: healthySp);
        var passingReport = await healthyVerifier.VerifyLiveAsync(
            treeDir,
            "Verify valid AggregateScreen view, Playwright DOM session, and loopback health",
            uiTouchedFiles,
            new EngineerLiveContext("trace-ui-ok", null, [("app.tasks", liveTasksScreen)]),
            Ct);

        Assert.True(passingReport.Succeeded, passingReport.Summary);
        Assert.True(passingReport.HostHealthy);
        Assert.True(passingReport.McpProbesPassed);
        Assert.True(passingReport.PlaywrightPassed);
        Assert.Equal(0, passingReport.Http4xxCount);
        Assert.Equal(0, passingReport.Http5xxCount);
        Assert.Equal(0, passingReport.ErrorSpanCount);
        Assert.Contains(passingReport.Probes, static p => p.Name == "ui-view-tree-gate" && p.Passed);
        Assert.Contains(passingReport.Probes, static p => p.Name == "ui-loopback-http-gate" && p.Passed);
        Assert.Contains(passingReport.Probes, static p => p.Name == "playwright-page-session" && p.Passed);

        var passingScore = EngineerCandidateScore.Compute(
            new string('a', 40),
            "--- a/ui/lib/src/nodes/app_shell.dart\n+++ b/ui/lib/src/nodes/app_shell.dart\n@@ -1 +1 @@\n-// old\n+// new\n",
            changedLines: 2,
            new EngineerBuildReport(true, true, true, true, 10, 0, 0, 0, [], 50, "OK"),
            passingReport);
        Assert.True(passingScore.LiveVerifyPassed);
        Assert.True(passingScore.AllGatesPassed);

        // Broken UI Case 1: Broken AppShellNode layout (empty key/automationName & empty Column body) + duplicate button keys!
        var brokenShellJson = JsonDocument.Parse(
            """
            {
              "title": "Broken Studio",
              "root": {
                "kind": "appShell",
                "key": "",
                "automationName": "",
                "body": { "kind": "column", "children": [] },
                "toolbar": {
                  "kind": "row",
                  "children": [
                    { "kind": "button", "key": "dup-btn", "label": "One", "automationName": "One" },
                    { "kind": "button", "key": "dup-btn", "label": "Two", "automationName": "" }
                  ]
                }
              }
            }
            """).RootElement.Clone();
        var brokenShellScreen = new FakeVerifyScreen(brokenShellJson);

        var brokenShellReport = await healthyVerifier.VerifyLiveAsync(
            treeDir,
            "Detect broken AppShellNode and missing AutomationName/duplicate key invariants",
            uiTouchedFiles,
            new EngineerLiveContext("trace-ui-broken-shell", null, [("app.engineer", brokenShellScreen)]),
            Ct);

        Assert.False(brokenShellReport.Succeeded);
        Assert.False(brokenShellReport.McpProbesPassed);
        Assert.False(brokenShellReport.PlaywrightPassed);
        Assert.Contains(brokenShellReport.Probes, static p => p.Name == "ui-view-tree-gate" && !p.Passed);
        var failedScreenProbe = Assert.Single(brokenShellReport.Probes, static p => p.Name == "mcp-screen-app.engineer");
        Assert.False(failedScreenProbe.Passed);
        Assert.Contains("AppShellNode", failedScreenProbe.Detail, StringComparison.Ordinal);
        Assert.Contains("dup-btn", failedScreenProbe.Detail, StringComparison.Ordinal);

        // Broken UI Case 2: HTTP 503 loopback failure prevents LiveVerifyPassed = true!
        var failingHttpFactory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var failingHttpSp = new ServiceCollection()
            .AddSingleton<IConfiguration>(healthyConfig)
            .AddSingleton<IHttpClientFactory>(failingHttpFactory)
            .BuildServiceProvider();
        using var failingHttpVerifier = new EngineerLiveVerifier(services: failingHttpSp);

        var http503Report = await failingHttpVerifier.VerifyLiveAsync(
            treeDir,
            "Detect HTTP 503 loopback failure",
            uiTouchedFiles,
            new EngineerLiveContext("trace-ui-503", null, [("app.tasks", liveTasksScreen)]),
            Ct);

        Assert.False(http503Report.Succeeded);
        Assert.True(http503Report.Http5xxCount >= 1);
        var failingScore = EngineerCandidateScore.Compute(
            new string('b', 40),
            "+// change\n",
            changedLines: 1,
            new EngineerBuildReport(true, true, true, true, 10, 0, 0, 0, [], 50, "OK"),
            http503Report);
        Assert.False(failingScore.LiveVerifyPassed);
        Assert.False(failingScore.ZeroTelemetryErrors);
    }

    [Fact]
    public void BuildVerifier_ParsesTrxCountsAndFailingTestNames()
    {
        const string trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="6bfebc1f-930e-4796-b65c-d5e9a13b1e48" name="agent@host" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Demo.CalculatorFacts.Add_ReturnsSum" outcome="Passed" duration="00:00:00.0100000" />
                <UnitTestResult testName="Demo.CalculatorFacts.Divide_ByZero_Throws" outcome="Failed" duration="00:00:00.0250000">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Throws() Failure: Expected typeof(DivideByZeroException) but no exception was thrown.</Message>
                      <StackTrace>at Demo.CalculatorFacts.Divide_ByZero_Throws() in CalculatorFacts.cs:line 42</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testName="Demo.CalculatorFacts.Multiply_LargeNumbers" outcome="Passed" duration="00:00:00.0050000" />
                <UnitTestResult testName="Demo.CalculatorFacts.FutureFeature_Skipped" outcome="NotExecuted" duration="00:00:00.0000000" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="4" executed="3" passed="2" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="1" />
              </ResultSummary>
            </TestRun>
            """;

        var parsed = EngineerBuildVerifier.ParseTrxXml(trxXml);

        Assert.Equal(2, parsed.Passed);
        Assert.Equal(1, parsed.Failed);
        Assert.Equal(1, parsed.Skipped);
        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Contains("Demo.CalculatorFacts.Divide_ByZero_Throws", diagnostic, StringComparison.Ordinal);
        Assert.Contains("DivideByZeroException", diagnostic, StringComparison.Ordinal);

        var trxPath = Path.Combine(_root, "sample-results.trx");
        File.WriteAllText(trxPath, trxXml, Encoding.UTF8);
        var fromFile = EngineerBuildVerifier.ParseTrxFile(trxPath);
        Assert.Equal(2, fromFile.Passed);
        Assert.Equal(1, fromFile.Failed);
        Assert.Equal(1, fromFile.Skipped);
        Assert.Single(fromFile.Diagnostics);
    }

    private sealed class FakeTraceReader(TraceEntry[] entries) : ITraceReader
    {
        public Task<TracePage> ReadTrace(string traceId, long since, int limit)
            => Task.FromResult(new TracePage(entries));
    }

    private sealed class FakeVerifyScreen(JsonElement view) : IScreen
    {
        public Task<PublishedView?> Open() => Read();

        public Task<PublishedView?> Read(string? alias = null)
            => Task.FromResult<PublishedView?>(new PublishedView(alias ?? "app.engineer", 1, view));

        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;

        public Task Dispatch(DigitalBrain.UI.Command command) => Task.CompletedTask;

        public Task<PublishedView?> ReadChild(string path) => Task.FromResult<PublishedView?>(null);
    }

    private sealed class RecordingSandboxRunner(List<SandboxCommandRequest> sink) : ISandboxCommandRunner
    {
        public IReadOnlyList<string> BuildContainerArguments(WorkTreeLease lease, SandboxCommandRequest request)
            => ["run", "--rm", request.Executable, .. request.Arguments];

        public Task<SandboxCommandResult> ExecuteAsync(WorkTreeLease lease, SandboxCommandRequest request, CancellationToken ct = default)
        {
            lock (sink)
            {
                sink.Add(request);
            }

            return Task.FromResult(new SandboxCommandResult(
                ExitCode: 0,
                StdOut: "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
                StdErr: string.Empty,
                ExecutedMode: SandboxExecutionMode.Container,
                EffectiveInvocation: ["docker", "run", "--rm", request.Executable, .. request.Arguments],
                DurationMs: 12));
        }
    }

    private sealed class SingleFileRoslynCoder : ICoder
    {
        private readonly IWorktreeRoslyn _roslyn = new WorktreeRoslynAnalyzer();

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            var edit = await _roslyn.ApplyEditsAsync(
                lease.TreePath,
                [
                    new WorktreeCSharpEdit(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "code/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: "Multiply",
                        Source: "public static int Multiply(int a, int b) => a * b;"),
                ],
                ct);
            Assert.True(edit.Succeeded, edit.Detail);
            return new CoderEditResult("Added Multiply via Roslyn.", edit.TouchedFiles, "qwen2.5-coder:14b");
        }
    }

    private sealed class TwoStepPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(string repositoryId, string goal, IReadOnlyList<string> allowedExtensions, int maxChangedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Two-step C# Roslyn update on Calculator",
                CriticReview: "Verify zero diagnostics and whitespace formatting",
                Transcript:
                [
                    new("User", "user", "Goal", goal),
                    new("Architect", "architect", "Plan", "Add Subtract in t1-sub, then Modulo in t2-mod"),
                    new("Critic", "critic", "Check", "Verify Triple Git Lock and zero errors"),
                ],
                Tasks:
                [
                    new("t1-sub", "Add Subtract method", "csharp", ["code/Calculator.cs"], [], "Add Subtract"),
                    new("t2-mod", "Add Modulo method", "csharp", ["code/Calculator.cs"], ["t1-sub"], "Add Modulo"),
                ]));
    }

    private sealed class CheckpointingRoslynCoder(List<(string LeaseId, string Prompt)> log) : ICoder
    {
        private readonly IWorktreeRoslyn _roslyn = new WorktreeRoslynAnalyzer();

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            lock (log)
            {
                log.Add((lease.EffectiveLeaseId, goal));
            }

            var isStep1 = goal.Contains("t1-sub", StringComparison.Ordinal);
            var memberName = isStep1 ? "Subtract" : "Modulo";
            var op = isStep1 ? "-" : "%";

            var edit = await _roslyn.ApplyEditsAsync(
                lease.TreePath,
                [
                    new WorktreeCSharpEdit(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "code/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: memberName,
                        Source: $"public static int {memberName}(int a, int b) => a {op} b;"),
                ],
                ct);

            Assert.True(edit.Succeeded, edit.Detail);
            return new CoderEditResult($"Added {memberName} via Roslyn.", edit.TouchedFiles, "qwen2.5-coder:14b");
        }
    }

    private sealed class SingleStepPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(string repositoryId, string goal, IReadOnlyList<string> allowedExtensions, int maxChangedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Single-step candidate tournament on Calculator",
                CriticReview: "Verify deterministic candidate scoring and SHA-bound promotion",
                Transcript: [new("User", "user", "Goal", goal)],
                Tasks: [new("t1-variant", "Implement Calculator variant", "csharp", ["code/Calculator.cs"], [], goal)]));
    }

    private sealed class TournamentVariantCoder : ICoder
    {
        private readonly IWorktreeRoslyn _roslyn = new WorktreeRoslynAnalyzer();

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            // Assert strategy variant prompt is injected when running parallel tournament instances
            Assert.Contains($"Candidate w{lease.InstanceIndex + 1} Strategy", goal, StringComparison.Ordinal);

            WorktreeCSharpEdit[] edits = lease.InstanceIndex == 0
                ? [
                    new(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "code/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: "Subtract",
                        Source: "public static int Subtract(int a, int b) => a - b;"),
                ]
                : [
                    new(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "code/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: "Subtract",
                        Source: "public static int Subtract(int a, int b) => checked(a - b);"),
                    new(
                        WorktreeCSharpEditKind.AddOrReplaceMember,
                        "code/Calculator.cs",
                        TypeName: "Calculator",
                        MemberName: "AbsDifference",
                        Source: "public static int AbsDifference(int a, int b) => a >= b ? a - b : b - a;"),
                ];

            var result = await _roslyn.ApplyEditsAsync(lease.TreePath, edits, ct);
            Assert.True(result.Succeeded, result.Detail);
            return new CoderEditResult($"Candidate w{lease.InstanceIndex + 1} applied {edits.Length} member(s).", result.TouchedFiles, "tournament-coder");
        }
    }

    private sealed class FailFirstInstanceBuildVerifier : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        {
            var calcSource = File.ReadAllText(Path.Combine(worktreePath, "code", "Calculator.cs"));
            var isFirst = !calcSource.Contains("AbsDifference", StringComparison.Ordinal);
            return Task.FromResult(isFirst
                ? new EngineerBuildReport(false, false, true, false, 0, 1, 0, 1, ["Simulated w1 build failure"], 30, "Candidate w1 build failed")
                : new EngineerBuildReport(true, true, true, true, 8, 0, 0, 0, [], 45, "Candidate w2 build & tests passed"));
        }
    }

    private sealed class DeterministicBuildVerifier : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerBuildReport(true, true, true, true, 12, 0, 0, 0, [], 85, "Release build & tests green"));
    }

    private sealed class DeterministicLiveVerifier : ILiveVerifier
    {
        public Task<EngineerLiveReport> VerifyLiveAsync(string worktreePath, string goal, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerLiveReport(
                true,
                true,
                true,
                true,
                0,
                0,
                0,
                0,
                [new EngineerLiveProbe("mcp-health", "mcp", true, 200, "200 OK")],
                ["span engineer.live status=ok"],
                "Live verify green (0 4xx/5xx)"));
    }

    private sealed class QueueInference(Queue<Func<InferenceRequest, AiMessage>> turns) : IModelInference
    {
        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor("Ollama", "qwen2.5-coder:14b", "default", "local", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new InferenceResult([turns.Dequeue()(request)], "stop", null, null, "qwen2.5-coder:14b"));

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => throw new NotSupportedException();
    }

    private sealed class BarrierParallelSliceCoder(
        IWorktreeRoslyn roslyn,
        int expectedParticipants,
        Action<string> onEnter,
        Action onExit,
        Task barrier) : ICoder
    {
        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            onEnter(goal);
            try
            {
                if (expectedParticipants > 1)
                {
                    await barrier.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }

                var (relPath, typeName, memberSource) = goal.Contains("slice-add", StringComparison.Ordinal)
                    ? ("code/Adder.cs", "Adder", "public static int Add(int a, int b) => a + b;")
                    : goal.Contains("slice-mul", StringComparison.Ordinal)
                        ? ("code/Multiplier.cs", "Multiplier", "public static int Multiply(int a, int b) => a * b;")
                        : ("code/Clamper.cs", "Clamper", "public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);");

                var summary = await roslyn.ApplyEditsAsync(
                    lease.TreePath,
                    [
                        new WorktreeCSharpEdit(
                            WorktreeCSharpEditKind.CreateTypeFile,
                            relPath,
                            TypeName: typeName,
                            NamespaceName: "Demo",
                            Source: memberSource,
                            TypeKind: "class"),
                    ],
                    ct);

                Assert.True(summary.Succeeded, summary.Detail);
                return new CoderEditResult($"Created {typeName}.", summary.TouchedFiles, "ino-native-slice");
            }
            finally
            {
                onExit();
            }
        }
    }

    private sealed class MutatingCliSandboxRunner(List<SandboxCommandRequest> sink) : ISandboxCommandRunner
    {
        public IReadOnlyList<string> BuildContainerArguments(WorkTreeLease lease, SandboxCommandRequest request)
            => ["run", "--rm", request.Executable, .. request.Arguments];

        public async Task<SandboxCommandResult> ExecuteAsync(WorkTreeLease lease, SandboxCommandRequest request, CancellationToken ct = default)
        {
            lock (sink)
            {
                sink.Add(request);
            }

            var calcPath = Path.Combine(lease.TreePath, "code", "Calculator.cs");
            await File.WriteAllTextAsync(
                calcPath,
                "namespace Demo;\npublic static class Calculator { public static int Add(int a, int b) => a + b; public static int Subtract(int a, int b) => a - b; }\n",
                ct);

            var jsonOut = """
                {"type":"progress","message":"Inspecting worktree"}
                {"type":"result","result":"Applied Subtract via stream-json."}
                """;
            return new SandboxCommandResult(0, jsonOut, string.Empty, SandboxExecutionMode.ConfinedHost, [request.Executable, .. request.Arguments], 18);
        }
    }

    private sealed class FakeWorkingMemory : IWorkingMemory
    {
        public int StartupPackReadCount { get; private set; }

        public int BriefReadCount { get; private set; }

        public Task<WorkingMemorySnapshot> Read() => Task.FromResult(new WorkingMemorySnapshot());

        public Task<WorkingMemoryStartupPack> ReadStartupPack()
        {
            StartupPackReadCount++;
            return Task.FromResult(new WorkingMemoryStartupPack
            {
                Revision = 1,
                Soul = "Ino surgical engineering persona.",
                User = "Prefers zero-bloat DDD records.",
                Project = "IntoChat OS repository",
                Scratchpad = new WorkingMemoryScratchpad(["Wave 5 InoNativeHarness"], ["Windows net11.0"], []),
                AvailableBriefs = [new ContextBriefSummary("os-conventions", "os Coding Conventions", "project", 64, DateTimeOffset.UtcNow)],
            });
        }

        public Task<WorkingMemoryStartupPack> FreezeSession(string sessionId) => ReadStartupPack();

        public Task<WorkingMemoryScratchpad> UpsertScratchpad(WorkingMemorySection section, string entry, string? replaceMatching = null)
            => Task.FromResult(new WorkingMemoryScratchpad());

        public Task<WorkingMemoryScratchpad> RemoveScratchpad(WorkingMemorySection section, string match)
            => Task.FromResult(new WorkingMemoryScratchpad());

        public Task<WorkingMemoryScratchpad> SetScratchpad(WorkingMemoryScratchpad scratchpad)
            => Task.FromResult(scratchpad);

        public Task<WorkingMemorySlice> SetSlice(WorkingMemorySliceKind kind, string content)
            => Task.FromResult(new WorkingMemorySlice(kind, content, DateTimeOffset.UtcNow));

        public Task<WorkingMemorySlice?> GetSlice(WorkingMemorySliceKind kind)
            => Task.FromResult<WorkingMemorySlice?>(null);

        public Task<ContextBriefSummary> PutBrief(string key, string title, string category, string content)
            => Task.FromResult(new ContextBriefSummary(key, title, category, content.Length, DateTimeOffset.UtcNow));

        public Task<bool> RemoveBrief(string key) => Task.FromResult(true);

        public Task<ContextBrief?> GetBrief(string key)
        {
            BriefReadCount++;
            return Task.FromResult<ContextBrief?>(new ContextBrief(
                key,
                "os Coding Conventions",
                "project",
                "Prefer pure static methods and zero compiler warnings.",
                DateTimeOffset.UtcNow));
        }

        public Task<ContextBriefSummary[]> ListBriefs(string? category = null)
            => Task.FromResult<ContextBriefSummary[]>([]);
    }

    private sealed class FakeSessionJournal : ISessionJournal
    {
        public List<SessionMilestone> Milestones { get; } = [];

        public Task<SessionJournalBlock> OpenSession(string? conversationId = null, string? goal = null, string? projectBrief = null, string? date = null)
            => Task.FromResult(new SessionJournalBlock { SessionNumber = 1, Goal = goal ?? "" });

        public Task<SessionJournalBlock> RecordMilestone(SessionMilestone milestone)
        {
            lock (Milestones)
            {
                Milestones.Add(milestone);
            }

            return Task.FromResult(new SessionJournalBlock
            {
                SessionNumber = 1,
                Goal = milestone.Goal ?? "",
                ProjectBrief = milestone.ProjectBrief,
                Deliverables = milestone.Deliverables ?? [],
                Decisions = milestone.Decisions ?? [],
                OpenThreads = milestone.OpenThreads ?? [],
            });
        }

        public Task<SessionJournalBlock> FinalizeSession(int? sessionNumber = null, string? date = null, string[]? openThreads = null)
            => Task.FromResult(new SessionJournalBlock { SessionNumber = sessionNumber ?? 1, Status = SessionBlockStatus.Finalized });

        public Task FollowAgent(string agentKey) => Task.CompletedTask;

        public Task<SessionJournalDay?> ReadDay(string? date = null) => Task.FromResult<SessionJournalDay?>(null);

        public Task<SessionReturningView> ReadReturningView(string? todayDate = null) => Task.FromResult(new SessionReturningView());

        public Task<string[]> ListDates() => Task.FromResult<string[]>([]);
    }

    private sealed class RepoBriefWorkingMemory(string briefKey, string briefContent) : IWorkingMemory
    {
        public Task<WorkingMemorySnapshot> Read() => Task.FromResult(new WorkingMemorySnapshot());

        public Task<WorkingMemoryStartupPack> ReadStartupPack()
            => Task.FromResult(new WorkingMemoryStartupPack
            {
                Revision = 1,
                Project = "IntoChat OS Repository",
                AvailableBriefs = [new ContextBriefSummary(briefKey, "Repository Brief", "project", briefContent.Length, DateTimeOffset.UtcNow)],
            });

        public Task<WorkingMemoryStartupPack> FreezeSession(string sessionId) => ReadStartupPack();

        public Task<WorkingMemoryScratchpad> UpsertScratchpad(WorkingMemorySection section, string entry, string? replaceMatching = null)
            => Task.FromResult(new WorkingMemoryScratchpad());

        public Task<WorkingMemoryScratchpad> RemoveScratchpad(WorkingMemorySection section, string match)
            => Task.FromResult(new WorkingMemoryScratchpad());

        public Task<WorkingMemoryScratchpad> SetScratchpad(WorkingMemoryScratchpad scratchpad)
            => Task.FromResult(scratchpad);

        public Task<WorkingMemorySlice> SetSlice(WorkingMemorySliceKind kind, string content)
            => Task.FromResult(new WorkingMemorySlice(kind, content, DateTimeOffset.UtcNow));

        public Task<WorkingMemorySlice?> GetSlice(WorkingMemorySliceKind kind)
            => Task.FromResult<WorkingMemorySlice?>(null);

        public Task<ContextBriefSummary> PutBrief(string key, string title, string category, string content)
            => Task.FromResult(new ContextBriefSummary(key, title, category, content.Length, DateTimeOffset.UtcNow));

        public Task<bool> RemoveBrief(string key) => Task.FromResult(true);

        public Task<ContextBrief?> GetBrief(string key)
            => Task.FromResult<ContextBrief?>(string.Equals(key, briefKey, StringComparison.OrdinalIgnoreCase)
                ? new ContextBrief(briefKey, "Repository Brief", "project", briefContent, DateTimeOffset.UtcNow)
                : null);

        public Task<ContextBriefSummary[]> ListBriefs(string? category = null)
            => Task.FromResult<ContextBriefSummary[]>([new ContextBriefSummary(briefKey, "Repository Brief", "project", briefContent.Length, DateTimeOffset.UtcNow)]);
    }

    private sealed class TurnOneErrorTurnTwoRepairCoder(List<string> observedPrompts) : ICoder
    {
        private int _turn;

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            var currentTurn = Interlocked.Increment(ref _turn);
            lock (observedPrompts)
            {
                observedPrompts.Add(goal);
            }

            var targetFile = Path.Combine(lease.TreePath, "code", "Calculator.cs");
            if (currentTurn == 1)
            {
                await File.WriteAllTextAsync(
                    targetFile,
                    """
                    namespace Demo;

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
                        public static int Subtract(int a, int b) => "not_an_int";
                    }
                    """,
                    ct);
                return new CoderEditResult("Drafted Subtract (with accidental string return).", ["code/Calculator.cs"], "ino-repair-test");
            }

            await File.WriteAllTextAsync(
                targetFile,
                """
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                    public static int Subtract(int a, int b) => a - b;
                }
                """,
                ct);
            return new CoderEditResult("Repaired CS0029 return type on Subtract.", ["code/Calculator.cs"], "ino-repair-test");
        }
    }

    private sealed class AlwaysSyntaxBrokenCoder : ICoder
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _invocations);
            var targetFile = Path.Combine(lease.TreePath, "code", "Calculator.cs");
            await File.WriteAllTextAsync(
                targetFile,
                """
                namespace Demo;

                public static class Calculator
                {
                    public static int Broken() => 42
                }
                """,
                ct);
            return new CoderEditResult("Still missing semicolon.", ["code/Calculator.cs"], "ino-broken-test");
        }
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new DelegatingStubHandler(responder));

        private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(responder(request));
        }
    }

    private sealed class FakeBrowserPageSession(bool connected, BrowserObservation observation) : IBrowserPageSession
    {
        public bool Connected => connected;

        public string? Url => observation.Url;

        public string? Title => observation.Title;

        public Task<BrowserObservation> NavigateAsync(string url, CancellationToken ct) => Task.FromResult(observation);

        public Task<BrowserObservation> SnapshotAsync(CancellationToken ct) => Task.FromResult(observation);

        public Task<BrowserObservation> ClickAsync(string selector, CancellationToken ct) => Task.FromResult(observation);

        public Task<BrowserObservation> FillAsync(string selector, string value, CancellationToken ct) => Task.FromResult(observation);

        public Task<string> EvaluateAsync(string expression, CancellationToken ct) => Task.FromResult("{}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
