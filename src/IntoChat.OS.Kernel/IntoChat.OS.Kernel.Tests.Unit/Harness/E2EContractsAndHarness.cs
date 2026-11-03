#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TaskStatus = IntoChat.OS.Modules.AI.Tasks.TaskStatus;
using TaskStepStatus = IntoChat.OS.Modules.AI.Tasks.TaskStepStatus;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;

#region F15: Decision Maker Subsystem (Delegates to Production IntoChat.OS.Modules.AI.Decision.DecisionMaker)

public sealed class InMemoryDecisionMaker : IDecisionMaker
{
    private readonly TestSystemOne _systemOne = new();
    private readonly TestRouter _router = new();
    private readonly DecisionMaker _inner;
    private readonly List<string> _auditTrail = [];

    public InMemoryDecisionMaker()
    {
        var services = new ServiceCollection()
            .AddSingleton<ISystemOne>(_systemOne)
            .AddSingleton<IRouter>(_router)
            .BuildServiceProvider();

        var ctxProvider = new ExecutionContextProvider();
        _inner = new DecisionMaker(services, ctxProvider, NullLogger<DecisionMaker>.Instance);
    }

    public IReadOnlyList<string> AuditTrail => _auditTrail.AsReadOnly();

    public void RegisterReactiveRule(string intent, string choice, double confidence)
        => _systemOne.RegisterRule(intent, choice, confidence);

    public async Task<FastDecision> DecideReactiveAsync(ReactivePrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();
        _auditTrail.Add($"Reactive:{prompt.Intent}");

        var expectedChoice = _systemOne.MatchChoice(prompt.Intent, prompt.CandidateOperations);
        var normalized = prompt.CandidateOperations.Contains(expectedChoice, StringComparer.OrdinalIgnoreCase)
            ? prompt
            : prompt with { CandidateOperations = [.. prompt.CandidateOperations, expectedChoice] };

        return await _inner.DecideReactiveAsync(normalized, ct).ConfigureAwait(false);
    }

    public async Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ct.ThrowIfCancellationRequested();
        _auditTrail.Add($"Deliberate:{goal.Objective}");

        var outcome = await _router.Achieve(goal.Objective, goal.AvailableCapabilities, ct).ConfigureAwait(false);
        return new DeliberateDecision($"Plan for: {goal.Objective}", outcome);
    }

    public Task<DecisionResult> DecideAsync(DecisionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        return _inner.DecideAsync(context, ct);
    }

    private sealed class TestSystemOne : ISystemOne
    {
        private readonly ConcurrentDictionary<string, (string Choice, double Confidence)> _rules = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterRule(string intent, string choice, double confidence)
            => _rules[intent] = (choice, confidence);

        public string MatchChoice(string intent, string[] candidates)
            => _rules.TryGetValue(intent, out var r) ? r.Choice : (candidates.Length > 0 ? candidates[0] : "execute_default");

        public Task<SystemOneResult> Ask(SystemOneRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_rules.TryGetValue(request.State, out var matched))
            {
                var probs = new Dictionary<string, double>
                {
                    [matched.Choice] = matched.Confidence,
                    ["fallback"] = Math.Max(0.0, 1.0 - matched.Confidence)
                };
                return Task.FromResult(new SystemOneResult("system-one-test", new()
                {
                    ["choice"] = new SystemOneAnswer.Choice(matched.Choice, probs, matched.Confidence)
                }));
            }

            var first = request.Questions.Values.OfType<SystemOneQuestion.Choice>().FirstOrDefault()?.Criteria.Keys.FirstOrDefault() ?? "execute_default";
            var defaultProbs = new Dictionary<string, double>
            {
                [first] = 0.95,
                ["defer"] = 0.05
            };
            return Task.FromResult(new SystemOneResult("system-one-test", new()
            {
                ["choice"] = new SystemOneAnswer.Choice(first, defaultProbs, 0.95)
            }));
        }
    }

    private sealed class TestRouter : IRouter
    {
        public Task Ask(RouteRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<PendingRoute?> Pending(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<PendingRoute?>(null);
        }

        public Task<RouteResult> Route(string state, string[]? allowed = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RouteResult("kernel", "execute", 1.0, new Dictionary<string, double> { ["kernel.execute"] = 1.0 }));
        }

        public Task<RouteOutcome> Dispatch(string state, string[]? allowed = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var route = new RouteResult("kernel", "execute", 1.0, new Dictionary<string, double> { ["kernel.execute"] = 1.0 });
            return Task.FromResult(new RouteOutcome(route, true, "local", null, "Dispatched", null));
        }

        public Task<GoalRun> Achieve(string goal, string[]? capabilities = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var steps = new[]
            {
                new GoalStep("kernel", "deconstruct", null, goal, "Objective deconstructed", null, 1.0),
                new GoalStep("kernel", "assess", null, $"Capabilities: {capabilities?.Length ?? 0}", "Capabilities assessed", null, 1.0),
                new GoalStep("kernel", "synthesize", null, null, "Plan synthesized", null, 1.0)
            };
            return Task.FromResult(new GoalRun(goal, steps, $"Synthesized plan for: {goal}"));
        }
    }
}

#endregion

#region F17: Task Engine for Self-Improvement (Delegates to Production TaskEngine + TaskNeuron + TaskDirectoryNeuron)

public sealed class InMemoryTaskEngine : ITaskEngine, IAsyncDisposable
{
    private readonly TestOS _os;
    private readonly TaskEngine _inner;
    private readonly HashSet<string> _knownTaskIds = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryTaskEngine()
    {
        _os = TestOS.Local();
        _os.Register<TaskNeuron>();
        _os.Register<TaskDirectoryNeuron>();
        _inner = new TaskEngine(
            taskId => _os.Get<ITaskNeuron>(new NeuronId(new BrainId("local"), "tasks.task", taskId)),
            () => _os.Get<ITaskDirectoryNeuron>(new NeuronId(new BrainId("local"), "tasks.directory", "default")));
    }

    public async Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var created = await _inner.CreateTaskAsync(request, ct).ConfigureAwait(false);
        lock (_knownTaskIds)
        {
            _knownTaskIds.Add(created.TaskId);
        }
        return created;
    }

    public async Task<AgentTask> AdvanceTaskStepAsync(string taskId, int stepIndex, TaskStepStatus newStatus, string? output = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        EnsureExists(taskId);

        var current = await _inner.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (current is not null && (stepIndex < 0 || stepIndex >= current.Steps.Length))
        {
            return current;
        }

        return await _inner.AdvanceTaskStepAsync(taskId, stepIndex, newStatus, output, ct).ConfigureAwait(false);
    }

    public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        return _inner.GetTaskAsync(taskId, ct);
    }

    public Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? owner = null, TaskStatus? status = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _inner.ListTasksAsync(owner, status, ct);
    }

    public Task<AgentTask> RollbackTaskAsync(string taskId, int toStepIndex, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        EnsureExists(taskId);
        return _inner.RollbackTaskAsync(taskId, toStepIndex, reason, ct);
    }

    public Task<AgentTask> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        EnsureExists(taskId);
        return _inner.CancelTaskAsync(taskId, reason, ct);
    }

    public Task<TaskVerificationResult> VerifyTaskStepAsync(string taskId, int stepIndex, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        return _inner.VerifyTaskStepAsync(taskId, stepIndex, ct);
    }

    public Task<AgentTask> ConfirmTaskStepAsync(string taskId, int stepIndex, string? confirmationToken = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();
        EnsureExists(taskId);
        return _inner.ConfirmTaskStepAsync(taskId, stepIndex, confirmationToken, ct);
    }

    public async Task<TaskVerificationResult> VerifyTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        bool exists;
        lock (_knownTaskIds)
        {
            exists = _knownTaskIds.Contains(taskId);
        }
        if (!exists)
        {
            return new TaskVerificationResult(false, "Verification failed", [$"Task '{taskId}' missing"]);
        }

        var task = await _inner.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return new TaskVerificationResult(false, "Verification failed", [$"Task '{taskId}' missing"]);
        }

        var failures = task.Steps
            .Where(s => s.Status != TaskStepStatus.Completed)
            .Select(s => $"Step '{s.Index}' [{s.Title}] did not pass (status: {s.Status})")
            .ToArray();

        var isPassed = failures.Length == 0 && task.Status == TaskStatus.Completed;
        return new TaskVerificationResult(isPassed, $"Verified {task.Steps.Length} steps.", failures);
    }

    private void EnsureExists(string taskId)
    {
        lock (_knownTaskIds)
        {
            if (!_knownTaskIds.Contains(taskId))
            {
                throw new KeyNotFoundException($"Task '{taskId}' not found.");
            }
        }
    }

    public ValueTask DisposeAsync() => _os.DisposeAsync();

    public Task<AgentTask> LinkTaskAsync(string taskId, string targetId, string kind, CancellationToken ct = default)
        => _inner.LinkTaskAsync(taskId, targetId, kind, ct);
}

#endregion

#region F20: Reusable Testing Framework for Agent Verification (Delegates to Production TestVerificationService)

public sealed class InMemoryTestVerificationService : ITestVerificationService
{
    private readonly TestVerificationService _inner = new(new E2ETestVerificationNeuron());

    public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.RunTargetedSuiteAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testSuiteId);
        if (string.IsNullOrWhiteSpace(testSuiteId))
        {
            throw new ArgumentException("TestSuiteId cannot be empty or whitespace.", nameof(testSuiteId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.GetHistoricalRunsAsync(testSuiteId, cancellationToken);
    }

    private sealed class E2ETestVerificationNeuron : ITestVerificationNeuron
    {
        private readonly ConcurrentDictionary<string, List<TestResultSummary>> _history = new(StringComparer.OrdinalIgnoreCase);

        public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new TestExecutionResult(
                Success: true,
                Total: 10,
                Passed: 10,
                Failed: 0,
                Skipped: 0,
                DurationMs: 42,
                Summary: "All tests executed successfully.",
                Failures: []);

            var list = _history.GetOrAdd(request.SuiteName, static _ => []);
            lock (list)
            {
                list.Add(new TestResultSummary(
                    RunId: $"run_{list.Count + 1}",
                    SuiteName: request.SuiteName,
                    ExecutedAt: DateTimeOffset.UtcNow,
                    Success: true,
                    Total: 10,
                    Passed: 10,
                    Failed: 0,
                    DurationMs: 42));
            }
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(testSuiteId);
            cancellationToken.ThrowIfCancellationRequested();
            if (_history.TryGetValue(testSuiteId, out var existing) && existing.Count > 0)
            {
                lock (existing)
                {
                    return Task.FromResult<IReadOnlyList<TestResultSummary>>(existing.ToList());
                }
            }

            return Task.FromResult<IReadOnlyList<TestResultSummary>>(
            [
                new TestResultSummary(
                    RunId: "run_1",
                    SuiteName: testSuiteId,
                    ExecutedAt: DateTimeOffset.UtcNow,
                    Success: true,
                    Total: 10,
                    Passed: 10,
                    Failed: 0,
                    DurationMs: 42)
            ]);
        }
    }
}

#endregion

#region F21: Aspire Telemetry Verification & Flush Barrier (Exercises TelemetryExportBarrier OpenTelemetry Pipeline)

public sealed record TelemetryMetricSnapshot(string MetricName, double Value, DateTimeOffset Timestamp);

public sealed class TelemetryFlushBarrier : IDisposable
{
    private static readonly ActivitySource BarrierSource = new(TelemetryExportBarrier.SourceName);
    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly ConcurrentBag<TelemetryMetricSnapshot> _metrics = [];
    private long _flushCount;

    public TelemetryFlushBarrier()
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(TelemetryExportBarrier.SourceName)
            .Build();
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .Build();
    }

    public long FlushCount => Interlocked.Read(ref _flushCount);

    public void RecordMetric(string name, double value)
    {
        _metrics.Add(new TelemetryMetricSnapshot(name, value, DateTimeOffset.UtcNow));
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var marker = Guid.NewGuid();
        using (var activity = BarrierSource.StartActivity(
            TelemetryExportBarrier.MarkerName,
            ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)))
        {
            activity?.SetTag(TelemetryExportBarrier.MarkerAttribute, marker.ToString("D"));
        }

        _tracerProvider.ForceFlush(5_000);
        _meterProvider.ForceFlush(5_000);
        var receipt = new TelemetryExportReceipt(marker, DateTimeOffset.UtcNow);
        if (receipt.Marker != Guid.Empty)
        {
            Interlocked.Increment(ref _flushCount);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<TelemetryMetricSnapshot> QueryMetrics(string name)
    {
        return _metrics.Where(m => string.Equals(m.MetricName, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Dispose()
    {
        _tracerProvider.Dispose();
        _meterProvider.Dispose();
    }
}

#endregion
