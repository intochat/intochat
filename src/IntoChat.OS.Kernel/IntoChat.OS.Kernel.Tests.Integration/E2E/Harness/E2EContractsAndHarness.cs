#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel.Tests.Integration.E2E.Harness;

#region F14: Execution Context Subsystem Contracts & In-Memory Double

public sealed record ContextSlice(string Name, string Content, string[] Tags);

public interface IExecutionContext
{
    string ExecutionId { get; }
    CapabilityCaller Caller { get; }
    IReadOnlyDictionary<string, string> Environment { get; }
    IReadOnlyList<ContextSlice> Slices { get; }
    CancellationToken CancellationToken { get; }
    Task<TResult> WithSliceAsync<TResult>(ContextSlice slice, Func<Task<TResult>> action);
}

public sealed class InMemoryExecutionContext : IExecutionContext
{
    private readonly List<ContextSlice> _slices;
    private readonly Dictionary<string, string> _environment;

    public InMemoryExecutionContext(
        string executionId,
        CapabilityCaller caller,
        IDictionary<string, string>? environment = null,
        IEnumerable<ContextSlice>? initialSlices = null,
        CancellationToken cancellationToken = default)
    {
        ExecutionId = executionId ?? Guid.NewGuid().ToString("N");
        Caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _environment = environment != null
            ? new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _slices = initialSlices != null ? [.. initialSlices] : [];
        CancellationToken = cancellationToken;
    }

    public string ExecutionId { get; }
    public CapabilityCaller Caller { get; }
    public IReadOnlyDictionary<string, string> Environment => _environment;
    public IReadOnlyList<ContextSlice> Slices => _slices.AsReadOnly();
    public CancellationToken CancellationToken { get; }

    public async Task<TResult> WithSliceAsync<TResult>(ContextSlice slice, Func<Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(action);

        _slices.Add(slice);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _slices.Remove(slice);
        }
    }
}

#endregion

#region F15: Decision Maker Subsystem Contracts & In-Memory Double

public sealed record ReactivePrompt(string Intent, IReadOnlyDictionary<string, string> Parameters, string Context = "");
public sealed record FastDecision(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities);

public sealed record DeliberateGoal(string Objective, string[] Constraints, int MaxIterations = 5);
public sealed record GoalRun(bool Succeeded, int StepsExecuted, string[] TraceLogs);
public sealed record DeliberateDecision(string Plan, GoalRun Outcome);

public interface IDecisionMaker
{
    Task<FastDecision> DecideReactiveAsync(ReactivePrompt prompt, CancellationToken ct = default);
    Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default);
}

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

        var defaultDecision = new FastDecision(
            Choice: "execute_default",
            Confidence: 0.95,
            Probabilities: new Dictionary<string, double>
            {
                ["execute_default"] = 0.95,
                ["defer"] = 0.05
            });

        return Task.FromResult(defaultDecision);
    }

    public Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ct.ThrowIfCancellationRequested();

        _auditTrail.Add($"Deliberate:{goal.Objective}");
        var logs = new[]
        {
            $"Deconstructed objective: {goal.Objective}",
            $"Assessed {goal.Constraints.Length} constraints",
            "Synthesized multi-step execution plan"
        };

        var outcome = new GoalRun(Succeeded: true, StepsExecuted: 3, TraceLogs: logs);
        var decision = new DeliberateDecision($"Plan for: {goal.Objective}", outcome);
        return Task.FromResult(decision);
    }
}

#endregion

#region F16: Knowledge Store & Graph Memory Contracts & In-Memory Double

public sealed record KnowledgeNode(string Id, string Label, string Type, IReadOnlyDictionary<string, string> Properties);
public sealed record KnowledgeEdge(string SourceId, string TargetId, string Relation, double Weight);
public sealed record KnowledgeGraphView(IReadOnlyList<KnowledgeNode> Nodes, IReadOnlyList<KnowledgeEdge> Edges);
public sealed record KnowledgeRecallHit(KnowledgeNode Node, double RelevanceScore);

public interface IKnowledgeStore
{
    Task RecordNodeAsync(KnowledgeNode node, CancellationToken ct = default);
    Task RecordEdgeAsync(KnowledgeEdge edge, CancellationToken ct = default);
    Task<KnowledgeGraphView> QueryGraphAsync(string rootId, int depth = 2, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeRecallHit>> SemanticSearchAsync(string query, int limit = 10, CancellationToken ct = default);
}

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

    public Task RecordEdgeAsync(KnowledgeEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();
        _edges.Add(edge);
        return Task.CompletedTask;
    }

    public Task<KnowledgeGraphView> QueryGraphAsync(string rootId, int depth = 2, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        ct.ThrowIfCancellationRequested();

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
        return Task.FromResult(new KnowledgeGraphView(matchedNodes, collectedEdges.Distinct().ToList()));
    }

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

#region F17: Task Engine for Self-Improvement Contracts & In-Memory Double

public enum AgentTaskStatus { Pending, InProgress, Succeeded, Failed, RolledBack }
public enum TaskStepStatus { Pending, Running, Passed, Failed, Skipped }

public sealed record TaskStep(string Id, string Description, TaskStepStatus Status, string? Output, string? Error);
public sealed record AgentTask(string Id, string Title, string Objective, AgentTaskStatus Status, TaskStep[] Steps, DateTimeOffset CreatedAt);
public sealed record CreateTaskRequest(string Title, string Objective, string[] StepDescriptions);
public sealed record TaskVerificationResult(bool IsValid, string[] FailedAssertions, string Logs);

public interface ITaskEngine
{
    Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task<AgentTask> UpdateStepStatusAsync(string taskId, string stepId, TaskStepStatus status, string? error = null, CancellationToken ct = default);
    Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? ownerPrincipalId = null, CancellationToken ct = default);
    Task<TaskVerificationResult> VerifyTaskAsync(string taskId, CancellationToken ct = default);
}

public sealed class InMemoryTaskEngine : ITaskEngine
{
    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new(StringComparer.OrdinalIgnoreCase);

    public Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var id = "task_" + Guid.NewGuid().ToString("N")[..8];
        var steps = request.StepDescriptions.Select((desc, idx) =>
            new TaskStep($"step_{idx + 1}", desc, TaskStepStatus.Pending, null, null)).ToArray();

        var task = new AgentTask(id, request.Title, request.Objective, AgentTaskStatus.Pending, steps, DateTimeOffset.UtcNow);
        _tasks[id] = task;
        return Task.FromResult(task);
    }

    public Task<AgentTask> UpdateStepStatusAsync(string taskId, string stepId, TaskStepStatus status, string? error = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var existing))
        {
            throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }

        var newSteps = existing.Steps.Select(step =>
        {
            if (string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase))
            {
                return step with { Status = status, Error = error, Output = status == TaskStepStatus.Passed ? "Step completed successfully" : null };
            }
            return step;
        }).ToArray();

        var overallStatus = existing.Status;
        if (newSteps.Any(s => s.Status == TaskStepStatus.Failed))
        {
            overallStatus = AgentTaskStatus.Failed;
        }
        else if (newSteps.All(s => s.Status == TaskStepStatus.Passed))
        {
            overallStatus = AgentTaskStatus.Succeeded;
        }
        else if (newSteps.Any(s => s.Status == TaskStepStatus.Running || s.Status == TaskStepStatus.Passed))
        {
            overallStatus = AgentTaskStatus.InProgress;
        }

        var updated = existing with { Steps = newSteps, Status = overallStatus };
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

    public Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? ownerPrincipalId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var all = _tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();
        return Task.FromResult<IReadOnlyList<AgentTask>>(all);
    }

    public Task<TaskVerificationResult> VerifyTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return Task.FromResult(new TaskVerificationResult(false, [$"Task '{taskId}' missing"], "Verification failed"));
        }

        var failures = new List<string>();
        foreach (var step in task.Steps)
        {
            if (step.Status != TaskStepStatus.Passed)
            {
                failures.Add($"Step '{step.Id}' [{step.Description}] did not pass (status: {step.Status})");
            }
        }

        var isValid = failures.Count == 0 && task.Status == AgentTaskStatus.Succeeded;
        return Task.FromResult(new TaskVerificationResult(isValid, [.. failures], $"Verified {task.Steps.Length} steps."));
    }
}

#endregion

#region F20: Reusable Testing Framework for Agent Verification

public sealed record TestExecutionRequest(string TargetTier, string? FeatureFilter = null);
public sealed record TestExecutionResult(bool AllPassed, int TotalExecuted, int PassedCount, int FailedCount, IReadOnlyList<string> Failures);

public interface ITestVerificationService
{
    Task<TestExecutionResult> RunTargetedAsync(TestExecutionRequest request, CancellationToken ct = default);
    Task<bool> VerifyMilestoneAsync(string milestoneId, CancellationToken ct = default);
}

public sealed class InMemoryTestVerificationService : ITestVerificationService
{
    public Task<TestExecutionResult> RunTargetedAsync(TestExecutionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var result = new TestExecutionResult(
            AllPassed: true,
            TotalExecuted: 10,
            PassedCount: 10,
            FailedCount: 0,
            Failures: []);

        return Task.FromResult(result);
    }

    public Task<bool> VerifyMilestoneAsync(string milestoneId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestoneId);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(true);
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
