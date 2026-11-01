#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Collections.Immutable;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps.Contracts.Tasks;
using IntoChat.OS.Modules.Apps.Contracts.Testing;
using TaskStatus = IntoChat.OS.Modules.Apps.Contracts.Tasks.TaskStatus;
using TaskStepStatus = IntoChat.OS.Modules.Apps.Contracts.Tasks.TaskStepStatus;

namespace IntoChat.OS.Kernel.Tests.Integration.E2E.Harness;

#region F14: Execution Context Subsystem In-Memory Double

public sealed class InMemoryExecutionContext : IExecutionContext
{
    private readonly AsyncLocal<ImmutableList<ContextSlice>?> _currentSlices = new();
    private readonly ImmutableList<ContextSlice> _rootSlices;
    private readonly Dictionary<string, string> _environment;

    public InMemoryExecutionContext(
        string executionId,
        CapabilityCaller? caller,
        IDictionary<string, string>? environment = null,
        IEnumerable<ContextSlice>? initialSlices = null,
        CancellationToken cancellationToken = default)
    {
        ExecutionId = executionId ?? Guid.NewGuid().ToString("N");
        Caller = caller;
        _environment = environment != null
            ? new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _rootSlices = initialSlices != null ? [.. initialSlices] : [];
        _currentSlices.Value = _rootSlices;
        CancellationToken = cancellationToken;
    }

    public string ExecutionId { get; }
    public CapabilityCaller? Caller { get; }
    public IReadOnlyDictionary<string, string> Environment => _environment;
    public IReadOnlyList<ContextSlice> Slices => _currentSlices.Value ?? _rootSlices;
    public CancellationToken CancellationToken { get; }

    public async Task<TResult> WithSliceAsync<TResult>(ContextSlice slice, Func<Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(action);

        var prev = _currentSlices.Value ?? _rootSlices;
        _currentSlices.Value = prev.Add(slice);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _currentSlices.Value = prev;
        }
    }
}

#endregion

#region F15: Decision Maker Subsystem In-Memory Double

public sealed class InMemoryDecisionMaker : IDecisionMaker
{
    private readonly ConcurrentDictionary<string, FastDecision> _reactiveOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _auditTrail = [];

    public IReadOnlyList<string> AuditTrail => _auditTrail.AsReadOnly();

    public void RegisterReactiveRule(string intent, string choice, double confidence)
    {
        _reactiveOverrides[intent] = new FastDecision(choice, confidence, new Dictionary<string, double>
        {
            [choice] = confidence,
            ["fallback"] = Math.Max(0.0, 1.0 - confidence)
        });
    }

    public Task<FastDecision> DecideReactiveAsync(ReactivePrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        _auditTrail.Add($"Reactive:{prompt.Intent}");
        if (_reactiveOverrides.TryGetValue(prompt.Intent, out var matched))
        {
            return Task.FromResult(matched);
        }

        var defaultChoice = prompt.CandidateOperations.Length > 0 ? prompt.CandidateOperations[0] : "execute_default";
        var defaultDecision = new FastDecision(
            Choice: defaultChoice,
            Confidence: 0.95,
            Probabilities: new Dictionary<string, double>
            {
                [defaultChoice] = 0.95,
                ["defer"] = 0.05
            });

        return Task.FromResult(defaultDecision);
    }

    public Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ct.ThrowIfCancellationRequested();

        _auditTrail.Add($"Deliberate:{goal.Objective}");
        var steps = new[]
        {
            new GoalStep("kernel", "deconstruct", null, goal.Objective, "Objective deconstructed", null, 1.0),
            new GoalStep("kernel", "assess", null, $"Capabilities: {goal.AvailableCapabilities?.Length ?? 0}", "Capabilities assessed", null, 1.0),
            new GoalStep("kernel", "synthesize", null, null, "Plan synthesized", null, 1.0)
        };

        var outcome = new GoalRun(goal.Objective, steps, $"Synthesized plan for: {goal.Objective}");
        var decision = new DeliberateDecision($"Plan for: {goal.Objective}", outcome);
        return Task.FromResult(decision);
    }

    public async Task<DecisionResult> DecideAsync(DecisionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Mode == DecisionMode.Deliberate)
        {
            var deliberate = await DecideDeliberateAsync(new DeliberateGoal(context.Intent, [.. context.Candidates], context.MaxSteps), ct).ConfigureAwait(false);
            return new DecisionResult(
                Choice: deliberate.Plan,
                Confidence: 1.0,
                ModeUsed: DecisionMode.Deliberate,
                Plan: deliberate.Plan,
                Outcome: deliberate.Outcome);
        }

        var reactivePrompt = new ReactivePrompt(context.Intent, [.. context.Candidates], context.Metadata);
        var fast = await DecideReactiveAsync(reactivePrompt, ct).ConfigureAwait(false);
        return new DecisionResult(
            Choice: fast.Choice,
            Confidence: fast.Confidence,
            ModeUsed: DecisionMode.Reactive,
            Probabilities: fast.Probabilities);
    }
}

#endregion

#region F16: Knowledge Store & Graph Memory In-Memory Double

public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, KnowledgeNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<KnowledgeEdge> _edges = [];

    public Task RecordNodeAsync(KnowledgeNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();
        _nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task<KnowledgeNode?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        _nodes.TryGetValue(id, out var node);
        return Task.FromResult<KnowledgeNode?>(node);
    }

    public Task DeleteNodeAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        _nodes.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task RecordEdgeAsync(KnowledgeEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();
        _edges.Add(edge);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeEdge>> QueryEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ct.ThrowIfCancellationRequested();

        var query = _edges.Where(e => direction switch
        {
            EdgeDirection.Outgoing => string.Equals(e.SourceId, nodeId, StringComparison.OrdinalIgnoreCase),
            EdgeDirection.Incoming => string.Equals(e.TargetId, nodeId, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(e.SourceId, nodeId, StringComparison.OrdinalIgnoreCase) || string.Equals(e.TargetId, nodeId, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return Task.FromResult<IReadOnlyList<KnowledgeEdge>>(query);
    }

    public Task DeleteEdgeAsync(string sourceId, string targetId, string relation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<KnowledgeGraphView> GetGraphViewAsync(string? rootId = null, int depth = 2, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rootId))
        {
            return Task.FromResult(new KnowledgeGraphView(_nodes.Values.ToList(), _edges.Distinct().ToList()));
        }

        if (!_nodes.ContainsKey(rootId))
        {
            return Task.FromResult(new KnowledgeGraphView([], [], rootId));
        }

        var visitedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        var activeNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        var collectedEdges = new List<KnowledgeEdge>();

        for (var step = 0; step < depth && activeNodes.Count > 0; step++)
        {
            var nextActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var current in activeNodes)
            {
                foreach (var edge in _edges)
                {
                    if (string.Equals(edge.SourceId, current, StringComparison.OrdinalIgnoreCase))
                    {
                        collectedEdges.Add(edge);
                        if (visitedNodes.Add(edge.TargetId))
                        {
                            nextActive.Add(edge.TargetId);
                        }
                    }
                    else if (string.Equals(edge.TargetId, current, StringComparison.OrdinalIgnoreCase))
                    {
                        collectedEdges.Add(edge);
                        if (visitedNodes.Add(edge.SourceId))
                        {
                            nextActive.Add(edge.SourceId);
                        }
                    }
                }
            }
            activeNodes = nextActive;
        }

        var matchedNodes = _nodes.Values.Where(n => visitedNodes.Contains(n.Id)).ToList();
        return Task.FromResult(new KnowledgeGraphView(matchedNodes, collectedEdges.Distinct().ToList(), rootId));
    }

    public Task<KnowledgeGraphView> QueryGraphAsync(string rootId, int depth = 2, CancellationToken ct = default)
        => GetGraphViewAsync(rootId, depth, ct);

    public Task<IReadOnlyList<KnowledgeRecallHit>> SemanticSearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        var hits = new List<KnowledgeRecallHit>();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var node in _nodes.Values)
        {
            var score = 0.0;
            foreach (var token in tokens)
            {
                if (node.Label.Contains(token, StringComparison.OrdinalIgnoreCase)) { score += 0.5; }
                if (node.Type.Contains(token, StringComparison.OrdinalIgnoreCase)) { score += 0.3; }
                if (node.Properties.Values.Any(p => p.Contains(token, StringComparison.OrdinalIgnoreCase))) { score += 0.2; }
            }

            if (score > 0)
            {
                hits.Add(new KnowledgeRecallHit(node, Math.Min(1.0, score)));
            }
        }

        var result = hits.OrderByDescending(h => h.RelevanceScore).Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeRecallHit>>(result);
    }
}

#endregion

#region F17: Task Engine for Self-Improvement In-Memory Double

public sealed class InMemoryTaskEngine : ITaskEngine
{
    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new(StringComparer.OrdinalIgnoreCase);

    public Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var id = "task_" + Guid.NewGuid().ToString("N")[..8];
        var steps = request.Steps.Select((stepDef, idx) =>
            new TaskStep(
                Index: idx,
                Title: stepDef.Title,
                Description: stepDef.Description,
                Capability: stepDef.Capability,
                Status: TaskStepStatus.Pending,
                RollbackCapability: stepDef.RollbackCapability,
                Output: null,
                StartedAt: null,
                CompletedAt: null)).ToArray();

        var task = new AgentTask(
            TaskId: id,
            Title: request.Title,
            Description: request.Description,
            Owner: request.Owner,
            Status: TaskStatus.Pending,
            Steps: steps,
            CreatedAt: DateTimeOffset.UtcNow);

        _tasks[id] = task;
        return Task.FromResult(task);
    }

    public Task<AgentTask> AdvanceTaskStepAsync(string taskId, int stepIndex, TaskStepStatus newStatus, string? output = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var existing))
        {
            throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }

        var newSteps = existing.Steps.Select(step =>
        {
            if (step.Index == stepIndex)
            {
                return step with
                {
                    Status = newStatus,
                    Output = output ?? (newStatus == TaskStepStatus.Completed ? "Step completed successfully" : null),
                    CompletedAt = newStatus is TaskStepStatus.Completed or TaskStepStatus.Failed ? DateTimeOffset.UtcNow : step.CompletedAt,
                    StartedAt = step.StartedAt ?? DateTimeOffset.UtcNow
                };
            }
            return step;
        }).ToArray();

        var overallStatus = existing.Status;
        if (newSteps.Any(s => s.Status == TaskStepStatus.Failed))
        {
            overallStatus = TaskStatus.Failed;
        }
        else if (newSteps.Length > 0 && newSteps.All(s => s.Status == TaskStepStatus.Completed))
        {
            overallStatus = TaskStatus.Completed;
        }
        else if (newSteps.Any(s => s.Status == TaskStepStatus.Running || s.Status == TaskStepStatus.Completed))
        {
            overallStatus = TaskStatus.Running;
        }

        var updated = existing with
        {
            Steps = newSteps,
            Status = overallStatus,
            CompletedAt = overallStatus is TaskStatus.Completed or TaskStatus.Failed ? DateTimeOffset.UtcNow : existing.CompletedAt
        };
        _tasks[taskId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? owner = null, TaskStatus? status = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var query = _tasks.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(owner))
        {
            query = query.Where(t => string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase));
        }
        if (status.HasValue)
        {
            query = query.Where(t => t.Status == status.Value);
        }
        var all = query.OrderByDescending(t => t.CreatedAt).ToList();
        return Task.FromResult<IReadOnlyList<AgentTask>>(all);
    }

    public Task<AgentTask> RollbackTaskAsync(string taskId, int toStepIndex, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var existing))
        {
            throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }

        var rolledBack = existing with
        {
            Status = TaskStatus.RolledBack,
            RollbackReason = reason
        };
        _tasks[taskId] = rolledBack;
        return Task.FromResult(rolledBack);
    }

    public Task<AgentTask> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var existing))
        {
            throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }

        var cancelled = existing with
        {
            Status = TaskStatus.Cancelled,
            RollbackReason = reason
        };
        _tasks[taskId] = cancelled;
        return Task.FromResult(cancelled);
    }

    public Task<TaskVerificationResult> VerifyTaskStepAsync(string taskId, int stepIndex, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return Task.FromResult(new TaskVerificationResult(false, "Task missing", [$"Task '{taskId}' missing"]));
        }

        var step = task.Steps.FirstOrDefault(s => s.Index == stepIndex);
        if (step == null)
        {
            return Task.FromResult(new TaskVerificationResult(false, $"Step {stepIndex} missing", [$"Step {stepIndex} not found in task '{taskId}'"]));
        }

        if (step.Status != TaskStepStatus.Completed)
        {
            return Task.FromResult(new TaskVerificationResult(false, $"Step {stepIndex} not completed", [$"Step {stepIndex} status is {step.Status}"]));
        }

        return Task.FromResult(new TaskVerificationResult(true, $"Step {stepIndex} verified successfully"));
    }

    public Task<TaskVerificationResult> VerifyTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return Task.FromResult(new TaskVerificationResult(false, "Verification failed", [$"Task '{taskId}' missing"]));
        }

        var failures = new List<string>();
        foreach (var step in task.Steps)
        {
            if (step.Status != TaskStepStatus.Completed)
            {
                failures.Add($"Step '{step.Index}' [{step.Title}] did not pass (status: {step.Status})");
            }
        }

        var isPassed = failures.Count == 0 && task.Status == TaskStatus.Completed;
        return Task.FromResult(new TaskVerificationResult(isPassed, $"Verified {task.Steps.Length} steps.", [.. failures]));
    }
}

#endregion

#region F20: Reusable Testing Framework for Agent Verification In-Memory Double

public sealed class InMemoryTestVerificationService : ITestVerificationService
{
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

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testSuiteId);
        if (string.IsNullOrWhiteSpace(testSuiteId))
        {
            throw new ArgumentException("TestSuiteId cannot be empty or whitespace.", nameof(testSuiteId));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var list = new List<TestResultSummary>
        {
            new TestResultSummary(
                RunId: "run_1",
                SuiteName: testSuiteId,
                ExecutedAt: DateTimeOffset.UtcNow,
                Success: true,
                Total: 10,
                Passed: 10,
                Failed: 0,
                DurationMs: 42)
        };

        return Task.FromResult<IReadOnlyList<TestResultSummary>>(list);
    }
}

#endregion

#region F21: Aspire Telemetry Verification & Flush Barrier

public sealed record TelemetryMetricSnapshot(string MetricName, double Value, DateTimeOffset Timestamp);

public sealed class TelemetryFlushBarrier
{
    private readonly ConcurrentBag<TelemetryMetricSnapshot> _metrics = [];
    private long _flushCount;

    public long FlushCount => Interlocked.Read(ref _flushCount);

    public void RecordMetric(string name, double value)
    {
        _metrics.Add(new TelemetryMetricSnapshot(name, value, DateTimeOffset.UtcNow));
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _flushCount);
        return Task.CompletedTask;
    }

    public IReadOnlyList<TelemetryMetricSnapshot> QueryMetrics(string name)
    {
        return _metrics.Where(m => string.Equals(m.MetricName, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

#endregion
