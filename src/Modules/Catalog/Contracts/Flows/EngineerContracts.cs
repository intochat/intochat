using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.start-request")]
public sealed record EngineerStartRequest(
    [property: Id(0)] string RunId,
    [property: Id(1)] string Repository,
    [property: Id(2)] string Goal,
    [property: Id(3)] int InstanceCount = 1,
    [property: Id(4)] string? RequestedBaseBranch = null)
{
    public EngineerStartRequest() : this("", "os", "", 1, null) { }

    public string ToStartJson() => System.Text.Json.JsonSerializer.Serialize(this, System.Text.Json.JsonSerializerOptions.Web);

    public Task<string> StartOnAsync(
        IFlows flows,
        string? title = null,
        string? parentFlowId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? Goal : title.Trim();
        var explicitKey = string.IsNullOrWhiteSpace(RunId) ? null : RunId.Trim();
        return flows.Start(IFlows.EngineerFlow, effectiveTitle, parentFlowId, ToStartJson(), ct, flowId: explicitKey);
    }
}

[GenerateSerializer, Alias("engineer.brainstorm-turn")]
public sealed record EngineerBrainstormTurn(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Role,
    [property: Id(2)] string Perspective,
    [property: Id(3)] string Message)
{
    public EngineerBrainstormTurn() : this("User", "user", "Goal", "") { }
}

[GenerateSerializer, Alias("engineer.task-item")]
public sealed record EngineerTaskItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Language,
    [property: Id(3)] string[] TargetFiles,
    [property: Id(4)] string[] DependsOn,
    [property: Id(5)] string Instructions)
{
    public EngineerTaskItem() : this("", "", "csharp", [], [], "") { }
}

[GenerateSerializer, Alias("engineer.task-plan")]
public sealed record EngineerTaskPlan(
    [property: Id(0)] string Goal,
    [property: Id(1)] string ArchitectureSummary,
    [property: Id(2)] string CriticReview,
    [property: Id(3)] EngineerBrainstormTurn[] Transcript,
    [property: Id(4)] EngineerTaskItem[] Tasks)
{
    public EngineerTaskPlan() : this("", "", "", [], []) { }

    public string? Validate(int maxChangedFiles = 5)
    {
        if (string.IsNullOrWhiteSpace(Goal))
        {
            return "EngineerTaskPlan requires a non-empty goal.";
        }

        if (Tasks is null || Tasks.Length == 0)
        {
            return "EngineerTaskPlan must contain at least one task.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var distinctTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id))
            {
                return "Every task in EngineerTaskPlan must have a non-empty id.";
            }

            if (!ids.Add(task.Id.Trim()))
            {
                return $"Duplicate task id '{task.Id}' in EngineerTaskPlan.";
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                return $"Task '{task.Id}' must have a non-empty title.";
            }

            var lang = (task.Language ?? string.Empty).Trim().ToLowerInvariant();
            if (lang is not ("csharp" or "dart" or "markdown" or "mixed"))
            {
                return $"Task '{task.Id}' has unsupported language '{task.Language}' (expected csharp, dart, markdown, or mixed).";
            }

            foreach (var rawFile in task.TargetFiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawFile))
                {
                    return $"Task '{task.Id}' contains a blank target file path.";
                }

                var clean = rawFile.Trim().Replace('\\', '/');
                if (clean.StartsWith('/') || Path.IsPathRooted(clean) || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
                {
                    return $"Task '{task.Id}' contains invalid or hidden target file path '{rawFile}'.";
                }

                distinctTargets.Add(clean);
            }
        }

        if (maxChangedFiles > 0 && distinctTargets.Count > maxChangedFiles)
        {
            return $"EngineerTaskPlan targets {distinctTargets.Count} distinct files, exceeding the policy maximum of {maxChangedFiles}.";
        }

        foreach (var task in Tasks)
        {
            foreach (var dep in task.DependsOn ?? [])
            {
                if (string.Equals(dep, task.Id, StringComparison.Ordinal))
                {
                    return $"Task '{task.Id}' cannot depend on itself.";
                }

                if (!ids.Contains(dep))
                {
                    return $"Task '{task.Id}' depends on unknown task '{dep}'.";
                }
            }
        }

        return TryComputeTopologicalOrder(out _)
            ? null
            : "EngineerTaskPlan contains a dependency cycle.";
    }

    public EngineerTaskItem[] TopologicalOrder()
        => TryComputeTopologicalOrder(out var ordered)
            ? ordered
            : throw new InvalidOperationException("Cannot order an EngineerTaskPlan containing a dependency cycle.");

    private bool TryComputeTopologicalOrder(out EngineerTaskItem[] ordered)
    {
        ordered = [];
        if (Tasks is null || Tasks.Length == 0)
        {
            return true;
        }

        var byId = Tasks.ToDictionary(static t => t.Id, StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var task in Tasks)
        {
            var deps = (task.DependsOn ?? []).Distinct(StringComparer.Ordinal).ToArray();
            inDegree[task.Id] = deps.Length;
            foreach (var dep in deps)
            {
                if (!byId.ContainsKey(dep))
                {
                    return false;
                }

                if (!dependents.TryGetValue(dep, out var list))
                {
                    list = [];
                    dependents[dep] = list;
                }

                list.Add(task.Id);
            }
        }

        var ready = new Queue<string>(Tasks.Where(t => inDegree[t.Id] == 0).Select(static t => t.Id));
        var result = new List<EngineerTaskItem>(Tasks.Length);

        while (ready.Count > 0)
        {
            var currentId = ready.Dequeue();
            result.Add(byId[currentId]);
            if (!dependents.TryGetValue(currentId, out var nextIds))
            {
                continue;
            }

            foreach (var nextId in nextIds)
            {
                inDegree[nextId]--;
                if (inDegree[nextId] == 0)
                {
                    ready.Enqueue(nextId);
                }
            }
        }

        if (result.Count != Tasks.Length)
        {
            return false;
        }

        ordered = [.. result];
        return true;
    }
}

[GenerateSerializer, Alias("engineer.build-report")]
public sealed record EngineerBuildReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool DotnetBuildPassed,
    [property: Id(2)] bool FlutterAnalyzePassed,
    [property: Id(3)] bool DotnetTestPassed,
    [property: Id(4)] int PassedTests,
    [property: Id(5)] int FailedTests,
    [property: Id(6)] int WarningCount,
    [property: Id(7)] int ErrorCount,
    [property: Id(8)] string[] Diagnostics,
    [property: Id(9)] long DurationMs,
    [property: Id(10)] string Summary)
{
    public EngineerBuildReport() : this(false, false, false, false, 0, 0, 0, 0, [], 0, "") { }
}

[GenerateSerializer, Alias("engineer.live-probe")]
public sealed record EngineerLiveProbe(
    [property: Id(0)] string Name,
    [property: Id(1)] string Kind,
    [property: Id(2)] bool Passed,
    [property: Id(3)] int? HttpStatus,
    [property: Id(4)] string Detail)
{
    public EngineerLiveProbe() : this("", "mcp", false, null, "") { }
}

[GenerateSerializer, Alias("engineer.live-report")]
public sealed record EngineerLiveReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool HostHealthy,
    [property: Id(2)] bool McpProbesPassed,
    [property: Id(3)] bool PlaywrightPassed,
    [property: Id(4)] int Http4xxCount,
    [property: Id(5)] int Http5xxCount,
    [property: Id(6)] int ErrorSpanCount,
    [property: Id(7)] int ErrorLogCount,
    [property: Id(8)] EngineerLiveProbe[] Probes,
    [property: Id(9)] string[] TraceSummaries,
    [property: Id(10)] string Summary)
{
    public EngineerLiveReport() : this(false, false, false, false, 0, 0, 0, 0, [], [], "") { }
}

public interface IEngineerPlanner
{
    Task<EngineerTaskPlan> PlanAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct = default);
}

public interface IEngineerBuildVerifier
{
    Task<EngineerBuildReport> VerifyAsync(
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}

public interface ILiveVerifier
{
    Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}
