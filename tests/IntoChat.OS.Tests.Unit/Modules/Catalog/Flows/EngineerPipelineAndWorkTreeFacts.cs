using System.Diagnostics;
using System.Security;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Tests;
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
        serviceCollection.AddSingleton<IWorktreeRoslyn>(_ => IWorktreeRoslyn.CreateDefault());
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner")));
        os.Register(typeof(AppsModule).Assembly);

        var flows = os.Get<IFlows>(IFlows.Key("owner"));
        var startJson = JsonSerializer.Serialize(new
        {
            runId = "engflow01",
            repository = "os",
            goal = "Add Subtract and Modulo methods to Calculator",
            instanceCount = 2,
        }, EngineerFlow.Json);

        var flowId = await flows.Start(IFlows.EngineerFlow, "Parallel Calculator Upgrade", null, startJson, Ct, flowId: "engflow01");
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
        os.Register(typeof(AppsModule).Assembly);

        var flows = os.Get<IFlows>(IFlows.Key("owner"));
        var startJson = JsonSerializer.Serialize(
            Change.Start("f2pr01", RepositoryId.Parse("os"), "Add Multiply via consolidated adapter"),
            FeatureToPrFlow.Json);

        var flowId = await flows.Start(FeatureToPrFlow.Alias, "Consolidated Change Run", null, startJson, Ct, flowId: "f2pr01");
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

        using var verifier = new EngineerLiveVerifier();
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
        serviceCollection.AddSingleton<ILiveVerifier>(new EngineerLiveVerifier());
        serviceCollection.AddSingleton<IWorktreeRoslyn>(_ => IWorktreeRoslyn.CreateDefault());
        await using var services = serviceCollection.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        var brain = new BrainId("owner");
        using var agentCaller = os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), brain));
        os.Register(typeof(AppsModule).Assembly);

        var flows = os.Get<IFlows>(IFlows.Key(brain.Value));
        var started = await flows.StartEngineerRun(
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
            var check = await flows.InspectEngineerRun(started.FlowId, Ct);
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
        Assert.Contains("neuron://owner/apps/engineer#owner/flow.engineer/inodeleg01", waiting.EngineerAppUri, StringComparison.Ordinal);

        // IInoStudio.ChangeStatus also inspects flow.engineer runs seamlessly!
        var studio = os.Get<IInoStudio>(new NeuronId(brain, "ino.studio", "ino/studio"));
        var studioStatus = await studio.ChangeStatus("inodeleg01", Ct);
        Assert.True(studioStatus.Ok, studioStatus.Text);
        Assert.NotNull(studioStatus.Card);
        Assert.Equal("approval", studioStatus.Card!.Kind);

        // Clean up active worktrees via human cancel
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        await flows.Cancel(started.FlowId, "test cleanup", Ct);
        await os.Idle();
    }

    private sealed class FakeTraceReader(TraceEntry[] entries) : ITraceReader
    {
        public Task<TracePage> ReadTrace(string traceId, long since, int limit)
            => Task.FromResult(new TracePage(entries));
    }

    private sealed class FakeVerifyScreen(JsonElement view) : IScreen
    {
        public Task<PublishedView?> Open() => Read();

        public Task Act(ScreenAction action) => Task.CompletedTask;

        public Task<PublishedView?> Read(string? alias = null)
            => Task.FromResult<PublishedView?>(new PublishedView(alias ?? "app.engineer", 1, view));

        public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null) => Task.CompletedTask;

        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;

        public Task Dispatch(DigitalBrain.UI.Command command) => Task.CompletedTask;

        public Task<PublishedView?> ReadSlot(string path) => Task.FromResult<PublishedView?>(null);
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
        private readonly IWorktreeRoslyn _roslyn = IWorktreeRoslyn.CreateDefault();

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
        private readonly IWorktreeRoslyn _roslyn = IWorktreeRoslyn.CreateDefault();

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

        public Task<ModelDescriptor> Describe(ModelSelection? selection, Type? marker, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor("Ollama", "qwen2.5-coder:14b", "default", "local", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported));

        public Task<InferenceResult> Generate(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new InferenceResult([turns.Dequeue()(request)], "stop", null, null, "qwen2.5-coder:14b"));

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId)
            => throw new NotSupportedException();
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
