using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class EngineerFlowRepairFacts : IDisposable
{
    private readonly string _root;

    public EngineerFlowRepairFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "intochat-eng-repair-" + Guid.NewGuid().ToString("N")[..8]);
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

    private sealed class EngineerRepairFixture(
        IWorkTree workTree,
        IEngineerPlanner planner,
        IEngineeringHarness harness,
        IEngineerBuildVerifier buildVerifier,
        ILiveVerifier liveVerifier) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton(workTree);
            services.AddSingleton(planner);
            services.AddSingleton(harness);
            services.AddSingleton(buildVerifier);
            services.AddSingleton(liveVerifier);
            services.AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer());
        }
    }

    private sealed class SingleTaskPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
        {
            return Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Update Calculator.cs cleanly.",
                CriticReview: "Approved.",
                Transcript: [],
                Tasks:
                [
                    new(
                        Id: "t1",
                        Title: "Implement Divide",
                        Language: "csharp",
                        TargetFiles: ["code/Calculator.cs"],
                        DependsOn: [],
                        Instructions: "Add Divide(int a, int b) to Calculator."),
                ]));
        }
    }

    private sealed class ScriptedRepairHarness(bool fixOnSecondRepairRound) : IEngineeringHarness
    {
        private readonly List<EngineeringHarnessRequest> _requests = [];
        private int _calls;

        public EngineeringHarnessKind Kind => EngineeringHarnessKind.InoNative;

        public IReadOnlyList<EngineeringHarnessRequest> Requests => _requests;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<EngineeringHarnessResult> ExecuteAsync(
            EngineeringHarnessRequest request,
            Func<OperationProgressed, Task>? onProgress = null,
            CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _calls);
            lock (_requests)
            {
                _requests.Add(request);
            }

            var target = Path.Combine(request.WorktreePath, "code", "Calculator.cs");
            if (fixOnSecondRepairRound && callNumber >= 3)
            {
                // Call 1 = initial implement-tasks; Call 2 = repair round 1 (still buggy); Call 3 = repair round 2 (fixed!)
                await File.WriteAllTextAsync(
                    target,
                    """
                    namespace Demo;

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
                        public static int Divide(int a, int b) => b == 0 ? throw new DivideByZeroException() : a / b;
                    }
                    """,
                    ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(
                    target,
                    $$"""
                    namespace Demo;

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
                        // Attempt {{callNumber}}
                        public static int Divide(int a, int b) => a - b;
                    }
                    """,
                    ct).ConfigureAwait(false);
            }

            var slices = (request.RatifiedPlan?.Slices ?? [])
                .Select(static s => s with { Status = "completed", Percent = 100 })
                .ToArray();
            return new EngineeringHarnessResult(
                Succeeded: true,
                Harness: EngineeringHarnessKind.InoNative,
                Summary: $"Harness attempt #{callNumber}",
                ModelId: "scripted-repair-model",
                TouchedFiles: ["code/Calculator.cs"],
                DeletedFiles: [],
                CompletedSlices: slices,
                ProgressEvents: []);
        }
    }

    private sealed class ContentInspectingBuildVerifier : IEngineerBuildVerifier
    {
        private int _verifications;

        public int VerificationCount => Volatile.Read(ref _verifications);

        public async Task<EngineerBuildReport> VerifyAsync(
            string worktreePath,
            IReadOnlyList<string> changedFiles,
            CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _verifications);
            var source = await File.ReadAllTextAsync(Path.Combine(worktreePath, "code", "Calculator.cs"), ct).ConfigureAwait(false);
            if (source.Contains("DivideByZeroException", StringComparison.Ordinal))
            {
                return new EngineerBuildReport(
                    Succeeded: true,
                    DotnetBuildPassed: true,
                    FlutterAnalyzePassed: true,
                    DotnetTestPassed: true,
                    PassedTests: 4,
                    FailedTests: 0,
                    WarningCount: 0,
                    ErrorCount: 0,
                    Diagnostics: [],
                    DurationMs: 12,
                    Summary: $"Build & tests passed on check #{attempt}.");
            }

            return new EngineerBuildReport(
                Succeeded: false,
                DotnetBuildPassed: true,
                FlutterAnalyzePassed: true,
                DotnetTestPassed: false,
                PassedTests: 3,
                FailedTests: 1,
                WarningCount: 0,
                ErrorCount: 1,
                Diagnostics:
                [
                    "Failed test 'Demo.CalculatorFacts.Divide_ByZero_Throws': Expected DivideByZeroException on check #" + attempt,
                ],
                DurationMs: 15,
                Summary: $"Test failure on check #{attempt}: Demo.CalculatorFacts.Divide_ByZero_Throws failed.");
        }
    }

    private sealed class PassingLiveVerifier : ILiveVerifier
    {
        public Task<EngineerLiveReport> VerifyLiveAsync(
            string worktreePath,
            string goal,
            IReadOnlyList<string> changedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new EngineerLiveReport(
                Succeeded: true,
                HostHealthy: true,
                McpProbesPassed: true,
                PlaywrightPassed: true,
                Http4xxCount: 0,
                Http5xxCount: 0,
                ErrorSpanCount: 0,
                ErrorLogCount: 0,
                Probes: [],
                TraceSummaries: [],
                Summary: "Live verify green."));
    }

    private WorkTreeService CreateWorkTreeService(string subName)
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

        var wt = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VerifyFormatting = false,
        });
        wt.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));
        return wt;
    }

    [Fact]
    public async Task EngineerFlow_RepairsBuildFailureOnSecondRoundAndSucceeds()
    {
        var workTree = CreateWorkTreeService("repair-succeeds");
        var harness = new ScriptedRepairHarness(fixOnSecondRepairRound: true);
        var buildVerifier = new ContentInspectingBuildVerifier();

        await using var fixture = new EngineerRepairFixture(
            workTree,
            new SingleTaskPlanner(),
            harness,
            buildVerifier,
            new PassingLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new
        {
            runId = "engrepair01",
            repository = "os",
            goal = "Add Divide method to Calculator",
            instanceCount = 1,
        }, EngineerFlow.Json);

        var flowId = await flows.Start(IFlows.EngineerFlow, "Repair Build Failure", null, startJson, Ct, flowId: "engrepair01");
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Waiting, waiting.Status);
        Assert.Equal("approval-gate", waiting.WaitingStep);

        // Initial build (#1 fails) -> repair round 1 -> build #2 fails -> repair round 2 -> build #3 passes!
        Assert.Equal(3, buildVerifier.VerificationCount);
        Assert.Equal(3, harness.Requests.Count);
        Assert.Contains("Divide_ByZero_Throws", harness.Requests[1].Goal, StringComparison.Ordinal);
        Assert.Contains("Divide_ByZero_Throws", harness.Requests[2].Goal, StringComparison.Ordinal);

        var starter = runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId));
        var run = JsonSerializer.Deserialize<EngineerRun>((await starter.Head()).InnerJson, EngineerFlow.Json)!;
        var winner = Assert.Single(run.Instances);
        Assert.True(winner.Build?.Succeeded);
        Assert.NotNull(winner.Commit);
        Assert.Contains("DivideByZeroException", winner.Commit!.HostRenderedDiff, StringComparison.Ordinal);

        await flows.Cancel(flowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task EngineerFlow_FailsAfterMaxThreeBuildRepairRounds()
    {
        Assert.Equal(3, EngineerFlow.MaxBuildRepairRounds);

        var workTree = CreateWorkTreeService("repair-exhausted");
        var harness = new ScriptedRepairHarness(fixOnSecondRepairRound: false);
        var buildVerifier = new ContentInspectingBuildVerifier();

        await using var fixture = new EngineerRepairFixture(
            workTree,
            new SingleTaskPlanner(),
            harness,
            buildVerifier,
            new PassingLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new
        {
            runId = "engrepair02",
            repository = "os",
            goal = "Add Divide method to Calculator",
            instanceCount = 1,
        }, EngineerFlow.Json);

        var flowId = await flows.Start(IFlows.EngineerFlow, "Exhaust Repair Rounds", null, startJson, Ct, flowId: "engrepair02");
        var failed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Equal(["brainstorm-and-plan", "prepare-worktree", "implement-tasks"], failed.Done);
        Assert.Contains("check #4", failed.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // 1 initial build + 3 repair rounds = 4 total build verifications, and 1 initial implement + 3 repair harness calls = 4!
        Assert.Equal(4, buildVerifier.VerificationCount);
        Assert.Equal(4, harness.Requests.Count);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Contains("Divide_ByZero_Throws", harness.Requests[i].Goal, StringComparison.Ordinal);
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
