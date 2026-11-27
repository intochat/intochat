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

public sealed class EngineerFlowSelfReviewFacts : IDisposable
{
    private readonly string _root;

    public EngineerFlowSelfReviewFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "intochat-eng-selfreview-" + Guid.NewGuid().ToString("N")[..8]);
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
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class SelfReviewFixture(
        IWorkTree workTree,
        IEngineerPlanner planner,
        IEngineeringHarness harness,
        IEngineerBuildVerifier buildVerifier,
        ILiveVerifier liveVerifier) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [.. CatalogFamily.Modules, typeof(FlowsFactsModule)];

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
                ArchitectureSummary: "Update Worker.cs cleanly.",
                CriticReview: "Approved.",
                Transcript: [],
                Tasks:
                [
                    new(
                        Id: "t1",
                        Title: "Update Worker",
                        Language: "csharp",
                        TargetFiles: ["code/Worker.cs"],
                        DependsOn: [],
                        Instructions: "Add Execute method to Worker."),
                ]));
        }
    }

    private enum HarnessBehavior
    {
        CleanFirstTry,
        SwallowThenFixOnRepair,
        AlwaysViolatesSecret,
    }

    private sealed class ScriptedSelfReviewHarness(HarnessBehavior behavior) : IEngineeringHarness
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

            var target = Path.Combine(request.WorktreePath, "code", "Worker.cs");
            var code = behavior switch
            {
                HarnessBehavior.CleanFirstTry => """
                    namespace Demo;

                    public static class Worker
                    {
                        public static int Execute(int x) => x + 1;
                    }
                    """,
                HarnessBehavior.SwallowThenFixOnRepair when callNumber == 1 => """
                    using System;

                    namespace Demo;

                    public static class Worker
                    {
                        public static int Execute(int x)
                        {
                            try { return x + 1; }
                            catch (Exception) { }
                            return x;
                        }
                    }
                    """,
                HarnessBehavior.SwallowThenFixOnRepair => """
                    using System;

                    namespace Demo;

                    public static class Worker
                    {
                        public static int Execute(int x) => checked(x + 1);
                    }
                    """,
                HarnessBehavior.AlwaysViolatesSecret => $$"""
                    namespace Demo;

                    public static class Worker
                    {
                        // Attempt {{callNumber}}
                        public const string Token = "{{string.Concat("ghp", "_", new string('b', 36))}}";
                        public static int Execute(int x) => x + 1;
                    }
                    """,
                _ => throw new InvalidOperationException(),
            };

            await File.WriteAllTextAsync(target, code, ct).ConfigureAwait(false);

            var slices = (request.RatifiedPlan?.Slices ?? [])
                .Select(static s => s with { Status = "completed", Percent = 100 })
                .ToArray();
            return new EngineeringHarnessResult(
                Succeeded: true,
                Harness: EngineeringHarnessKind.InoNative,
                Summary: $"Harness attempt #{callNumber}",
                ModelId: "scripted-self-review-model",
                TouchedFiles: ["code/Worker.cs"],
                DeletedFiles: [],
                CompletedSlices: slices,
                ProgressEvents: []);
        }
    }

    private sealed class PassingBuildVerifier : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerBuildReport(true, true, true, true, 2, 0, 0, 0, [], 5, "Build & tests passed."));
    }

    private sealed class CountingLiveVerifier(bool failAfterFirstCall = false) : ILiveVerifier
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<EngineerLiveReport> VerifyLiveAsync(string worktreePath, string goal, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (failAfterFirstCall && call > 1)
            {
                return Task.FromResult(new EngineerLiveReport(
                    Succeeded: false,
                    HostHealthy: true,
                    McpProbesPassed: false,
                    PlaywrightPassed: false,
                    Http4xxCount: 0,
                    Http5xxCount: 1,
                    ErrorSpanCount: 1,
                    ErrorLogCount: 0,
                    Probes: [],
                    TraceSummaries: [],
                    Summary: $"Post-repair live verify failed on check #{call}."));
            }

            return Task.FromResult(new EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], [], $"Live verify green on check #{call}."));
        }
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
        File.WriteAllText(Path.Combine(checkout, "code", "Worker.cs"), """
            namespace Demo;

            public static class Worker
            {
                public static int Execute(int x) => x;
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
    public async Task EngineerFlow_CleanDiffPassesLocalSelfReviewToApprovalGate()
    {
        var workTree = CreateWorkTreeService("clean");
        var harness = new ScriptedSelfReviewHarness(HarnessBehavior.CleanFirstTry);
        var liveVerifier = new CountingLiveVerifier();

        await using var fixture = new SelfReviewFixture(workTree, new SingleTaskPlanner(), harness, new PassingBuildVerifier(), liveVerifier);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new { runId = "engsr01", repository = "os", goal = "Add Execute helper", instanceCount = 1 }, EngineerFlow.Json);
        var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Clean Self Review", null, startJson, Ct, flowId: "engsr01");

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Waiting, waiting.Status);
        Assert.Equal("approval-gate", waiting.WaitingStep);
        Assert.Single(harness.Requests);
        Assert.Equal(1, liveVerifier.CallCount);

        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var summary = await runs.InspectEngineerRun(flowId, Ct);
        Assert.NotNull(summary.SelfReview);
        Assert.False(summary.SelfReview!.HasBlocking);
        Assert.NotNull(summary.Receipt);
        Assert.True(summary.Receipt!.SelfReviewPassed);
        Assert.True(summary.Receipt.LiveVerified);
        Assert.Contains(summary.DiagnosticsAndTraces, line => line.Contains("self-review · passed", StringComparison.Ordinal));

        await flows.Cancel(flowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task EngineerFlow_SwallowViolationTriggersAutoRepairAndReRunsLiveVerifyBeforeApprovalGate()
    {
        var workTree = CreateWorkTreeService("repair");
        var harness = new ScriptedSelfReviewHarness(HarnessBehavior.SwallowThenFixOnRepair);
        var liveVerifier = new CountingLiveVerifier();

        await using var fixture = new SelfReviewFixture(workTree, new SingleTaskPlanner(), harness, new PassingBuildVerifier(), liveVerifier);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new { runId = "engsr02", repository = "os", goal = "Add Execute helper", instanceCount = 1 }, EngineerFlow.Json);
        var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Auto Repair Self Review", null, startJson, Ct, flowId: "engsr02");

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Waiting, waiting.Status);
        Assert.Equal("approval-gate", waiting.WaitingStep);
        Assert.Equal(2, harness.Requests.Count);
        Assert.Contains("HR-Swallow", harness.Requests[1].Goal, StringComparison.Ordinal);
        // LiveVerifier must be re-invoked for the amended commit SHA after self-review auto-repair.
        Assert.Equal(2, liveVerifier.CallCount);

        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var summary = await runs.InspectEngineerRun(flowId, Ct);
        Assert.NotNull(summary.SelfReview);
        Assert.False(summary.SelfReview!.HasBlocking);
        Assert.NotNull(summary.Receipt);
        Assert.True(summary.Receipt!.LiveVerified);
        Assert.Equal(1, Assert.Single(summary.Instances).SelfReviewRepairAttempts);
        Assert.Contains(summary.DiagnosticsAndTraces, line => line.Contains("after 1 repair round(s)", StringComparison.Ordinal));

        await flows.Cancel(flowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task EngineerFlow_AutoRepairFailsClosedWhenPostRepairLiveVerifyFails()
    {
        var workTree = CreateWorkTreeService("repair-live-fail");
        var harness = new ScriptedSelfReviewHarness(HarnessBehavior.SwallowThenFixOnRepair);
        var liveVerifier = new CountingLiveVerifier(failAfterFirstCall: true);

        await using var fixture = new SelfReviewFixture(workTree, new SingleTaskPlanner(), harness, new PassingBuildVerifier(), liveVerifier);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new { runId = "engsr04", repository = "os", goal = "Add Execute helper", instanceCount = 1 }, EngineerFlow.Json);
        var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Post Repair Live Verify Fail", null, startJson, Ct, flowId: "engsr04");

        var failed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Equal(2, liveVerifier.CallCount);
        Assert.Contains("post-repair live-verify", failed.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineerFlow_UnfixedSecretViolationFailsClosedBeforeApprovalGate()
    {
        var workTree = CreateWorkTreeService("fail-closed");
        var harness = new ScriptedSelfReviewHarness(HarnessBehavior.AlwaysViolatesSecret);

        await using var fixture = new SelfReviewFixture(workTree, new SingleTaskPlanner(), harness, new PassingBuildVerifier(), new CountingLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var startJson = JsonSerializer.Serialize(new { runId = "engsr03", repository = "os", goal = "Add Execute helper", instanceCount = 1 }, EngineerFlow.Json);
        var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Fail Closed Self Review", null, startJson, Ct, flowId: "engsr03");

        var failed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Contains("LocalSelfReview blocked before approval-gate", failed.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(LocalSelfReview.SecretRuleId, failed.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1 + EngineerFlow.MaxSelfReviewRepairRounds, harness.Requests.Count);
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
