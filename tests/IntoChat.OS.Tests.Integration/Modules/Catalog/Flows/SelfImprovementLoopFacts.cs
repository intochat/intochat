using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Modules.Catalog.Flows;

public sealed class SelfImprovementLoopFacts
{
    private static readonly Type[] LoopModules = Journey.SliceModules;

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task UnboundRequiredSlotRefusesActivationNamingTheSlot(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, LoopModules, f => f.Friends().Postgres(), ct);
        var ada = os.Person("ada");

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => loop.Status(ct));
        Assert.Equal(ActivationRefusedException.SlotUnbound, refused.Reason);
        Assert.Contains("findingsLog", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task BoundLoopActivatesAndReportsReady(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, LoopModules, f => f.Friends().Postgres(), ct);
        var ada = os.Person("ada");

        var findingsTableId = new NeuronId(ada.Brain, "storage", "loop-findings");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("loop.self-improvement"),
            [new Binding("findingsLog", new BindingTarget.Local(findingsTableId))],
            ct);

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        var status = await loop.Status(ct);
        Assert.Equal("ready", status);
    }

    [Fact]
    public async Task RunCycle_OrchestratesFindingsBudgetLessonsAndEngineerRunsEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeRuns = new RecordingEngineerRuns();

        await using var os = await JourneyOS.Start(
            NeuronRuntimeKind.TestOS,
            LoopModules,
            f => f.Friends().Postgres().Configure(s => s.AddSingleton<IEngineerRuns>(fakeRuns)),
            ct);
        var ada = os.Person("ada");

        var findingsTableId = new NeuronId(ada.Brain, "storage", "loop-findings");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("loop.self-improvement"),
            [new Binding("findingsLog", new BindingTarget.Local(findingsTableId))],
            ct);

        // Pre-seed a prior lesson for the highest-priority failing fact.
        var memory = ada.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(ada.Brain));
        await LoopLessons.RecordFailureAsync(
            memory,
            "fact:CalculatorFacts.DivideByZero",
            "Added unchecked division",
            "Still threw OverflowException for int.MinValue / -1",
            ct);

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        var result = await loop.RunCycle(new LoopCycleRequest(
            RepoSlug: "os",
            TraceSpans: [new LoopTraceSpan("t1", "s1", "Kernel", "sync.tick", "Error", 900, "TimeoutException")],
            OwedMarkdown: "- Clean up stale helper. Owner: loop.",
            FailingFacts: [new LoopFailingFact("CalculatorFacts.DivideByZero", "Expected DivideByZeroException")],
            MaxConcurrentRuns: 2), ct);

        Assert.True(result.Dispatched);
        Assert.Equal("dispatched", result.Status);
        Assert.Equal("fact:CalculatorFacts.DivideByZero", result.FindingKey);
        Assert.Equal("lesson:fact:CalculatorFacts.DivideByZero", result.InjectedLessonKey);
        Assert.Equal(1, result.RunsToday);
        Assert.Equal(1, result.ActiveRuns);

        var started = Assert.Single(fakeRuns.StartedRequests);
        Assert.Contains("CalculatorFacts.DivideByZero", started.Goal, StringComparison.Ordinal);
        Assert.Contains("Prior Loop Lessons", started.Goal, StringComparison.Ordinal);
        Assert.Contains("OverflowException", started.Goal, StringComparison.Ordinal);

        // Record outcome as completed -> records success lesson and clears active run count.
        var completed = await loop.RecordOutcome(
            "fact:CalculatorFacts.DivideByZero",
            result.FlowId!,
            succeeded: true,
            detail: "Fixed divide-by-zero guard and passed all tests.",
            tokensUsed: 1_250,
            ct);

        Assert.NotNull(completed);
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(1_250, completed.TokensUsed);

        var snapshot = await loop.Inspect(ct);
        Assert.Equal(0, snapshot.ActiveRunCount);
        Assert.Contains("fact:CalculatorFacts.DivideByZero", snapshot.AdmittedFindingKeys);

        var brief = await memory.GetBrief("lesson:fact:CalculatorFacts.DivideByZero");
        Assert.NotNull(brief);
        Assert.Contains("Resolved:", brief!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCycle_EnforcesPauseKillSwitchDailyBudgetAttemptsParkingAndNoFindingsRefusals()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeRuns = new RecordingEngineerRuns();

        await using var os = await JourneyOS.Start(
            NeuronRuntimeKind.TestOS,
            LoopModules,
            f => f.Friends().Postgres().Configure(s => s.AddSingleton<IEngineerRuns>(fakeRuns)),
            ct);
        var ada = os.Person("ada");

        var findingsTableId = new NeuronId(ada.Brain, "storage", "loop-findings");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("loop.self-improvement"),
            [new Binding("findingsLog", new BindingTarget.Local(findingsTableId))],
            ct);

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        // 1. Paused refusal path.
        await loop.SetPaused(true, ct);
        Assert.Equal("paused", await loop.Status(ct));
        var pausedResult = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:A", "fact", "A", "broken")]), ct);
        Assert.False(pausedResult.Dispatched);
        Assert.Equal("paused", pausedResult.Status);

        // 2. Kill-switch refusal path.
        await loop.SetPaused(false, ct);
        await loop.ConfigureBudget(new LoopBudget(RunsPerDay: 5, TokensPerRun: 10_000, AttemptsPerFinding: 2, KillSwitch: true), ct);
        Assert.Equal("killed", await loop.Status(ct));
        var killedResult = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:A", "fact", "A", "broken")]), ct);
        Assert.False(killedResult.Dispatched);
        Assert.Equal(LoopBudgetExceededException.KillSwitchCode, killedResult.Status);

        // 3. No-findings refusal path.
        await loop.ConfigureBudget(new LoopBudget(RunsPerDay: 1, TokensPerRun: 10_000, AttemptsPerFinding: 2, KillSwitch: false), ct);
        Assert.Equal("ready", await loop.Status(ct));
        var emptyResult = await loop.RunCycle(new LoopCycleRequest(), ct);
        Assert.False(emptyResult.Dispatched);
        Assert.Equal("no-findings", emptyResult.Status);

        // 4. Daily budget cap refusal path.
        var first = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:A", "fact", "A", "broken")],
            MaxConcurrentRuns: 3), ct);
        Assert.True(first.Dispatched);

        var capped = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:B", "fact", "B", "also broken")],
            MaxConcurrentRuns: 3), ct);
        Assert.False(capped.Dispatched);
        Assert.Equal(LoopBudgetExceededException.DailyBudgetCode, capped.Status);

        // 5. Attempts cap parks repeatedly failing finding.
        await loop.ConfigureBudget(new LoopBudget(RunsPerDay: 10, TokensPerRun: 10_000, AttemptsPerFinding: 2, KillSwitch: false), ct);
        await loop.RecordOutcome("fact:A", first.FlowId!, succeeded: false, detail: "Build failed attempt 1", ct: ct);
        var second = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:A", "fact", "A", "broken")]), ct);
        Assert.True(second.Dispatched);
        await loop.RecordOutcome("fact:A", second.FlowId!, succeeded: false, detail: "Build failed attempt 2", ct: ct);

        var parkedAttempt = await loop.RunCycle(new LoopCycleRequest(
            Findings: [new LoopFinding("fact:A", "fact", "A", "broken")]), ct);
        Assert.False(parkedAttempt.Dispatched);
        Assert.Equal(LoopBudgetExceededException.AttemptsBudgetCode, parkedAttempt.Status);

        var snapshot = await loop.Inspect(ct);
        Assert.Contains("fact:A", snapshot.ParkedFindingKeys);
    }

    private sealed class RecordingEngineerRuns : IEngineerRuns
    {
        private readonly List<EngineerDelegationRequest> _requests = [];
        private int _seq;

        public IReadOnlyList<EngineerDelegationRequest> StartedRequests => _requests;

        public Task<EngineerRunSummary> StartEngineerRun(EngineerDelegationRequest request, CancellationToken ct = default)
        {
            var id = Interlocked.Increment(ref _seq);
            lock (_requests)
            {
                _requests.Add(request);
            }

            var flowId = $"loop-brain/flow.engineer/{request.RunId ?? $"run-{id}"}";
            return Task.FromResult(MakeSummary(flowId, request.RunId ?? $"run-{id}", request.RepoSlug, request.Goal, FlowStatus.Waiting));
        }

        public Task<EngineerRunSummary> InspectEngineerRun(string? flowId = null, CancellationToken ct = default)
            => Task.FromResult(MakeSummary(flowId ?? "unknown", "run", "os", "inspect", FlowStatus.Waiting));

        public Task<EngineerRunSummary> SelectEngineerCandidate(string flowId, int candidateIndex, CancellationToken ct = default)
            => Task.FromResult(MakeSummary(flowId, "run", "os", "select", FlowStatus.Waiting));

        public Task<EngineerRunSummary> RatifyEngineerPlan(string flowId, string[]? approvedCleanupTargets = null, EngineerTaskSlice[]? slices = null, CancellationToken ct = default)
            => Task.FromResult(MakeSummary(flowId, "run", "os", "ratify", FlowStatus.Waiting));

        private static EngineerRunSummary MakeSummary(string flowId, string runId, string repoSlug, string goal, FlowStatus status) => new(
            Ok: true,
            FlowId: flowId,
            RunId: runId,
            RepoSlug: repoSlug,
            Goal: goal,
            Backend: "LocalGit",
            ParallelInstances: 1,
            Status: status,
            CurrentStage: "approval-gate",
            CompletedStages: ["brainstorm-and-plan", "prepare-worktree", "implement-tasks", "build-and-test", "live-verify"],
            ConfirmationPlanId: $"app:engineer:{runId}:sha",
            TraceId: "trace-1",
            WinningCommitSha: "0123456789abcdef0123456789abcdef01234567",
            PushedRef: null,
            PullRequestUrl: null,
            EngineerAppUri: "neuron://tasks",
            TasksBoardUri: "neuron://tasks",
            Plan: null,
            Instances: [],
            DiagnosticsAndTraces: [],
            SummaryText: "Engineer run waiting for approval.");
    }
}
