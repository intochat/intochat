using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Cluster;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.Aspire.Deployment;
using IntoChat.OS.Tests.Integration.Components.Aspire;
using IntoChat.Aspire.Verification;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Tests.Integration.Modules.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Tests.Integration.Journeys;

/// <summary>
/// Deterministic scripted end-to-end self-improvement acceptance journey running on both
/// <see cref="NeuronRuntimeKind.TestOS"/> and <see cref="NeuronRuntimeKind.Silo"/>:
/// finding discovery -> deduplication &amp; budget -> LocalOnly git worktree -> EngineerFlow
/// build-repair loop -> LocalSelfReview -> fail-closed EngineerLiveVerifier -> approval-gate card ->
/// scripted approval -> WorkTreeService.MergeLocalAsync -> CandidateNeuron/DeploymentNeuron promote &amp; rollback.
/// </summary>
public sealed class SelfImprovementAcceptanceJourney
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ScriptedSelfImprovementLoopFindsFixesVerifiesPromotesAndRollsBack(NeuronRuntimeKind kind)
    {
        Assert.Equal(3, EngineerFlow.MaxBuildRepairRounds);

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "intochat-accept-scripted-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. Seed a temporary LocalOnly git repository with a deliberate failing defect (no origin remote).
            var sourceRepo = Path.Combine(tempRoot, "source-repo");
            var worktreeHome = Path.Combine(tempRoot, "worktree-home");
            Directory.CreateDirectory(sourceRepo);
            Directory.CreateDirectory(worktreeHome);

            RunGit(sourceRepo, ["init", "--initial-branch=main"]);
            RunGit(sourceRepo, ["config", "user.name", "Acceptance Test"]);
            RunGit(sourceRepo, ["config", "user.email", "accept@example.test"]);

            Directory.CreateDirectory(Path.Combine(sourceRepo, "code"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceRepo, "code", "Calculator.cs"),
                """
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a - b;
                }
                """,
                Ct);

            RunGit(sourceRepo, ["add", "."]);
            RunGit(sourceRepo, ["commit", "-m", "Initial commit with seeded defect"]);
            var beforeSha = RunGit(sourceRepo, ["rev-parse", "refs/heads/main"]).Trim();

            var workTree = new WorkTreeService(options: new WorkTreeOptions
            {
                HomeDirectory = worktreeHome,
                VerifyFormatting = false,
            });
            workTree.RegisterRepository(new RegisteredRepository(
                Id: "os",
                LocalPath: sourceRepo,
                DefaultBaseBranch: "main",
                LocalOnly: true));

            // 2. Discover, deduplicate, and budget-admit the failing fact finding.
            var discovered = LoopFindingSources.FromFailingFacts(
            [
                ("Demo.CalculatorFacts.Add_ReturnsSum", "Expected Calculator.Add(2, 3) == 5, got -1 in code/Calculator.cs."),
            ]);
            var finding = Assert.Single(discovered);
            Assert.Equal("fact", finding.Source);

            var deduplicator = new FindingDeduplicator();
            Assert.True(deduplicator.TryAdmit(finding));
            Assert.False(deduplicator.TryAdmit(finding));

            var budget = new LoopBudgetTracker(new LoopBudget(
                RunsPerDay: 5,
                TokensPerRun: 50_000,
                AttemptsPerFinding: 3,
                KillSwitch: false));
            budget.RecordRunStarted(finding.Key);
            var recordedTokens = budget.RecordTokens(
                "accept-run-01",
                new InferenceUsage(InputTokens: 600, OutputTokens: 250, TotalTokens: 850));
            Assert.Equal(850, recordedTokens);

            // Prove EngineerLiveVerifier is fail-closed when no candidate host or endpoint is configured.
            using (var unconfiguredVerifier = new EngineerLiveVerifier())
            {
                var refused = await unconfiguredVerifier.VerifyLiveAsync(
                    sourceRepo,
                    "Verify fail-closed behavior",
                    ["code/Calculator.cs"],
                    Ct);
                Assert.False(refused.Succeeded);
                Assert.Contains("placebo verification is disabled", refused.Summary, StringComparison.OrdinalIgnoreCase);
            }

            // 3. Boot JourneyOS on kind (TestOS or Silo) and run EngineerFlow with repair round 1 -> 2.
            var harness = new ScriptedRepairHarness();
            var buildVerifier = new ContentInspectingBuildVerifier();
            var liveVerifierCalls = 0;
            using var liveVerifier = new EngineerLiveVerifier(
                candidateVerifier: (ctx, _) =>
                {
                    Interlocked.Increment(ref liveVerifierCalls);
                    Assert.NotNull(ctx.VerificationPlan);
                    Assert.Equal("/health", ctx.VerificationPlan!.HealthPath);
                    Assert.True(ctx.VerificationPlan.RequireZeroErrorSpans);
                    return Task.FromResult(new EngineerLiveReport(
                        Succeeded: true,
                        HostHealthy: true,
                        McpProbesPassed: true,
                        PlaywrightPassed: true,
                        Http4xxCount: 0,
                        Http5xxCount: 0,
                        ErrorSpanCount: 0,
                        ErrorLogCount: 0,
                        Probes:
                        [
                            new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> 200"),
                            new EngineerLiveProbe("candidate-mcp", "mcp", true, 200, "initialize + tools/list + tools/call ok"),
                        ],
                        TraceSummaries: ["span candidate.health status=OK", "span candidate.mcp status=OK"],
                        Summary: "Candidate healthy: /health 200, MCP probes passed, 0 error spans."));
                });

            var candidateHost = new TrackingCandidateHost(beforeSha);
            var clusterHost = new FakeClusterHost { Current = beforeSha };
            var candidateVerifier = new FakeVerifier { Passed = true };

            await using var os = await JourneyOS.Start(
                kind,
                [typeof(AppsModule), typeof(AspireModule), typeof(IntoChat.OS.Modules.Microsoft.Roslyn.RoslynModule)],
                f => f.Configure(services =>
                {
                    services.AddLogging();
                    services.AddJournalPayloads(typeof(IFlows).Assembly);
                    services.AddJournalPayloads(typeof(AppsModule).Assembly);
                    services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
                    services.AddJournalPayloads(typeof(FlowStarted).Assembly);
                    services.AddJournalPayloads(typeof(AspireModule).Assembly);
                    services.TryAddSingleton(TimeProvider.System);
                    services.AddSingleton<IWorkTree>(workTree);
                    services.AddSingleton<IEngineerPlanner>(new SingleTaskPlanner());
                    services.AddSingleton<IEngineeringHarness>(harness);
                    services.AddSingleton<IEngineerBuildVerifier>(buildVerifier);
                    services.AddSingleton<ILiveVerifier>(liveVerifier);
                    services.AddRoslynWorkspace();
                    services.AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer());
                    services.AddSingleton<ICandidateHost>(candidateHost);
                    services.AddSingleton<IClusterHost>(clusterHost);
                    services.AddSingleton<ICandidateVerifier>(candidateVerifier);
                    services.TryAddSingleton<IClusterMembershipFeed>(new StubClusterMembershipFeed());
                }),
                Ct);

            var brain = os.Runtime.Brain;
            using var personScope = os.Runtime.As(Who.Person);

            var flows = os.Runtime.Get<IFlows>(IFlows.Key(brain.Value));
            const string runId = "engaccept01";
            var startJson = JsonSerializer.Serialize(new
            {
                runId,
                repository = "os",
                goal = $"Fix finding {finding.Key}: {finding.Detail}",
                instanceCount = 1,
            }, EngineerFlow.Json);

            var flowId = await flows.Start(IEngineerRuns.EngineerFlow, "Acceptance Self-Improvement", null, startJson, Ct, flowId: runId);
            var waiting = await EventuallyAsync(
                () => flows.Read(flowId, Ct),
                v => v is { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed });

            Assert.NotNull(waiting);
            Assert.Equal(FlowStatus.Waiting, waiting!.Status);
            Assert.Equal("approval-gate", waiting.WaitingStep);

            // Round 1 failed build -> repair round 2 fixed and passed build!
            Assert.Equal(2, harness.Requests.Count);
            Assert.Equal(2, buildVerifier.VerificationCount);
            Assert.Contains("Add_ReturnsSum", harness.Requests[1].Goal, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref liveVerifierCalls));

            var starter = os.Runtime.Get<IFlowStarter>(NeuronId.Parse(flowId));
            var run = JsonSerializer.Deserialize<EngineerRun>((await starter.Head()).InnerJson, EngineerFlow.Json)!;
            var winner = Assert.Single(run.Instances);
            Assert.True(winner.Build?.Succeeded);
            Assert.True(winner.Live?.Succeeded);
            Assert.NotNull(winner.Lease);
            Assert.NotNull(winner.Commit);

            var approvedSha = winner.Commit!.Commit;
            Assert.NotEqual(beforeSha, approvedSha.Value);

            // Run LocalSelfReview on the fixed worktree and assert zero blocking findings.
            var changedPaths = winner.Commit.ChangedFiles.Select(static f => f.Value).ToArray();
            var roslyn = os.Runtime.Get<IntoChat.OS.Modules.Microsoft.Roslyn.IRoslyn>(new NeuronId(brain, "microsoft.roslyn", "self-review"));
            var selfReview = await LocalSelfReview.ReviewWorktree(roslyn, winner.Lease!.TreePath, changedPaths, Ct);
            Assert.False(selfReview.HasBlocking, $"Unexpected blocking self-review findings: {selfReview.FormatForCard()}");
            Assert.Empty(selfReview.Findings);

            // 4. Assert the pending approval card in IConfirmationInbox contains diff, commit SHA, and summary.
            var inbox = os.Runtime.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
            var pendingCards = await EventuallyAsync(
                async () => (await inbox.Read()).Pending,
                p => p.Length > 0);
            var card = Assert.Single(pendingCards);
            Assert.Contains(approvedSha.Value, card.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("a + b", card.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("Harness attempt #2", card.Plan.Body, StringComparison.Ordinal);

            // 5. While the flow is parked at approval-gate it still owns the winner lease, so the local
            //    fast-forward runs now: once the card resolves, EngineerFlow resumes push -> pr -> clean
            //    (or its failure path) and removes the worktree concurrently with any caller using it.
            var mergedSha = await workTree.MergeLocalAsync(winner.Lease, approvedSha, "main", Ct);
            Assert.Equal(approvedSha, mergedSha);

            var mainHeadAfterMerge = RunGit(sourceRepo, ["rev-parse", "refs/heads/main"]).Trim();
            Assert.Equal(approvedSha.Value, mainHeadAfterMerge);
            var mergedSource = await File.ReadAllTextAsync(Path.Combine(sourceRepo, "code", "Calculator.cs"), Ct);
            Assert.Contains("a + b", mergedSource, StringComparison.Ordinal);

            // Scripted approval resolves the card; the flow then owns and releases the worktree.
            await inbox.Resolve(card.Plan.Id, approve: true);
            var requests = os.Runtime.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
            var outcome = await EventuallyAsync(
                () => requests.Outcome(card.Plan.Id),
                static r => r is not null);
            Assert.NotNull(outcome);
            Assert.True(outcome!.Approved);

            var settled = await EventuallyAsync(
                () => flows.Read(flowId, Ct),
                static v => v is { Status: FlowStatus.Failed or FlowStatus.Completed });
            Assert.True(settled is { Status: FlowStatus.Failed or FlowStatus.Completed }, $"EngineerFlow did not settle after approval: {settled?.Status}");
            Assert.False(Directory.Exists(winner.Lease.TreePath), "EngineerFlow must release the candidate worktree after approval.");

            // 6. Exercise CandidateNeuron + DeploymentNeuron:
            //    (a) Promote moves refs/intochat-os/live to approvedSha and Finish completes on healthy reconcile.
            var goodCandidate = os.Runtime.Get<ICandidate>(
                new NeuronId(brain, "aspire.candidate", ICandidate.Relative(approvedSha.Value)));
            await goodCandidate.Propose(
                winner.Lease.BranchName,
                "Fix Calculator.Add",
                new VerificationPlan([], [], TimeSpan.FromSeconds(5)));
            var startedGood = await goodCandidate.Start();
            Assert.Equal(CandidateStage.Healthy, startedGood.Stage);
            var verifiedGood = await goodCandidate.Verify();
            Assert.Equal(CandidateStage.Verified, verifiedGood.Stage);
            var promotingGood = await goodCandidate.Promote();
            Assert.Equal(CandidateStage.Promoting, promotingGood.Stage);
            Assert.Equal(approvedSha.Value, candidateHost.Live);
            Assert.Equal(approvedSha.Value, clusterHost.Current);

            await goodCandidate.Finish();
            var promotedGood = await EventuallyAsync(
                () => goodCandidate.Read(),
                static s => s.Stage == CandidateStage.Promoted);
            Assert.Equal(CandidateStage.Promoted, promotedGood.Stage);
            Assert.Contains($"commit {approvedSha.Value}", candidateHost.Calls);

            //    (b) Subsequent candidate with failed post-restart health check triggers Abandon() + Uncommit(),
            //        restoring refs/intochat-os/live back to approvedSha!
            var badSha = "bad0" + approvedSha.Value[4..];
            var badCandidate = os.Runtime.Get<ICandidate>(
                new NeuronId(brain, "aspire.candidate", ICandidate.Relative(badSha)));
            await badCandidate.Propose(
                "ino/bad-candidate",
                "Candidate that fails post-restart health check",
                new VerificationPlan([], [], TimeSpan.FromSeconds(5)));
            Assert.Equal(CandidateStage.Healthy, (await badCandidate.Start()).Stage);
            Assert.Equal(CandidateStage.Verified, (await badCandidate.Verify()).Stage);
            Assert.Equal(CandidateStage.Promoting, (await badCandidate.Promote()).Stage);
            Assert.Equal(badSha, candidateHost.Live);

            await badCandidate.Abandon("post-restart health check failed");
            var abandoned = await EventuallyAsync(
                () => badCandidate.Read(),
                static s => s.Stage == CandidateStage.Failed);
            Assert.Equal(CandidateStage.Failed, abandoned.Stage);
            Assert.Contains("post-restart health check failed", abandoned.Failure ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(approvedSha.Value, candidateHost.Live);
            Assert.Contains(candidateHost.Calls, c => c.StartsWith($"uncommit {badSha}", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> predicate, int timeoutMs = 15_000)
    {
        var deadline = Stopwatch.StartNew();
        T last = await probe().ConfigureAwait(false);
        while (!predicate(last) && deadline.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25, Ct).ConfigureAwait(false);
            last = await probe().ConfigureAwait(false);
        }

        return last;
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
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
                ArchitectureSummary: "Fix Calculator.Add in code/Calculator.cs.",
                CriticReview: "Approved.",
                Transcript: [],
                Tasks:
                [
                    new(
                        Id: "t1",
                        Title: "Fix Calculator.Add",
                        Language: "csharp",
                        TargetFiles: ["code/Calculator.cs"],
                        DependsOn: [],
                        Instructions: "Change Calculator.Add(int a, int b) to return a + b."),
                ]));
        }
    }

    private sealed class ScriptedRepairHarness : IEngineeringHarness
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
            if (callNumber == 1)
            {
                // Round 1: leaves the defect in place so EngineerFlow exercises build-repair loop.
                await File.WriteAllTextAsync(
                    target,
                    """
                    namespace Demo;

                    public static class Calculator
                    {
                        // Round 1 attempt (still buggy)
                        public static int Add(int a, int b) => a - b;
                    }
                    """,
                    ct).ConfigureAwait(false);
            }
            else
            {
                // Round 2: fixes the defect cleanly.
                await File.WriteAllTextAsync(
                    target,
                    """
                    namespace Demo;

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
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
                ModelId: "scripted-acceptance-model",
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
            if (source.Contains("a + b", StringComparison.Ordinal))
            {
                return new EngineerBuildReport(
                    Succeeded: true,
                    DotnetBuildPassed: true,
                    FlutterAnalyzePassed: true,
                    DotnetTestPassed: true,
                    PassedTests: 3,
                    FailedTests: 0,
                    WarningCount: 0,
                    ErrorCount: 0,
                    Diagnostics: [],
                    DurationMs: 10,
                    Summary: $"Build & tests passed on check #{attempt}.");
            }

            return new EngineerBuildReport(
                Succeeded: false,
                DotnetBuildPassed: true,
                FlutterAnalyzePassed: true,
                DotnetTestPassed: false,
                PassedTests: 0,
                FailedTests: 1,
                WarningCount: 0,
                ErrorCount: 1,
                Diagnostics:
                [
                    $"Failed test 'Demo.CalculatorFacts.Add_ReturnsSum': Expected 5, got -1 on check #{attempt}",
                ],
                DurationMs: 12,
                Summary: $"Test failure on check #{attempt}: Demo.CalculatorFacts.Add_ReturnsSum failed.");
        }
    }

    private sealed class TrackingCandidateHost(string initialLiveSha) : ICandidateHost
    {
        private readonly Dictionary<string, string?> _previousLiveBySha = new(StringComparer.Ordinal);
        private int _prepared;

        public List<string> Calls { get; } = [];
        public string? Live { get; private set; } = initialLiveSha;

        public Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
        {
            Calls.Add("prepare");
            var sha = $"sha{++_prepared:0000}";
            return Task.FromResult(new PreparedCandidate(sha, $"worktrees/{sha}"));
        }

        public Task BuildGate(string sha, CancellationToken ct)
        {
            Calls.Add($"gate {sha}");
            return Task.CompletedTask;
        }

        public Task<string> Publish(string sha, CancellationToken ct)
        {
            Calls.Add($"publish {sha}");
            return Task.FromResult($"builds/{sha}");
        }

        public Task<RunningCandidate> Run(string sha, CancellationToken ct)
        {
            Calls.Add($"run {sha}");
            return Task.FromResult(new RunningCandidate(sha, new CandidateEndpoints("http://127.0.0.1:5191", "http://127.0.0.1:5190"), $"candidate-{sha}"));
        }

        public Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
        {
            Calls.Add($"healthy {sha}");
            return Task.CompletedTask;
        }

        public Task<string?> Commit(string sha, CancellationToken ct)
        {
            Calls.Add($"commit {sha}");
            var previous = Live;
            _previousLiveBySha[sha] = previous;
            Live = sha;
            return Task.FromResult(previous);
        }

        public Task Uncommit(string sha, string? previous, CancellationToken ct)
        {
            var resolvedPrevious = previous ?? (_previousLiveBySha.TryGetValue(sha, out var recorded) ? recorded : null);
            Calls.Add($"uncommit {sha} {resolvedPrevious}");
            if (Live == sha)
            {
                Live = resolvedPrevious;
            }

            return Task.CompletedTask;
        }

        public Task Remove(string sha, CancellationToken ct)
        {
            Calls.Add($"remove {sha}");
            return Task.CompletedTask;
        }

        public Task<string[]> Orphans(string[] known, CancellationToken ct)
        {
            Calls.Add("orphans");
            return Task.FromResult(Array.Empty<string>());
        }
    }

    private sealed class StubClusterMembershipFeed : IClusterMembershipFeed
    {
        public ClusterSnapshot Current { get; } = new([], 0);

        public IAsyncDisposable Subscribe(Func<ClusterSnapshot?, Task> changed) => NullDisposable.Instance;

        private sealed class NullDisposable : IAsyncDisposable
        {
            public static readonly NullDisposable Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
