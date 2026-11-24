using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.code-graph-digest")]
public sealed record RepositoryCodeGraphDigest(
    [property: Id(0)] string RepositoryId,
    [property: Id(1)] string CompactDigest,
    [property: Id(2)] int ByteCount,
    [property: Id(3)] string? MatchedBriefKey = null,
    [property: Id(4)] string[]? GroundedFiles = null,
    [property: Id(5)] string[]? PotentialCleanupTargets = null,
    [property: Id(6)] string[]? DuplicateSymbols = null)
{
    public const int MaxBytes = 2_048;

    public RepositoryCodeGraphDigest()
        : this("", "", 0, null, null, null, null)
    {
    }
}

[GenerateSerializer, Alias("engineer.collaborator")]
public sealed record MissionCollaborator(
    [property: Id(0)] string Id,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string Kind,
    [property: Id(3)] string? FocusNodeId = null,
    [property: Id(4)] bool Active = true)
{
    public MissionCollaborator()
        : this("", "", "person", null, true)
    {
    }
}

[GenerateSerializer, Alias("engineer.ratified-plan")]
public sealed record RatifiedPlan(
    [property: Id(0)] string Goal,
    [property: Id(1)] string ArchitectureSummary,
    [property: Id(2)] string[] CleanupTargets,
    [property: Id(3)] EngineerTaskSlice[] Slices,
    [property: Id(4)] bool Ratified = true,
    [property: Id(5)] string? RatifiedBy = null,
    [property: Id(6)] DateTimeOffset? RatifiedAt = null,
    [property: Id(7)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(8)] string? CodeGraphDigest = null)
{
    public RatifiedPlan()
        : this("", "", [], [], true, null, null, EngineeringHarnessKind.InoNative, null)
    {
    }

    public NoOverlappingFileSlices DisjointSlicesRule() => new(Slices ?? []);

    public EngineerTaskSlice[][] TopologicalWaves() => EngineerTaskSlices.ComputeWaves(Slices ?? []);
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
    [property: Id(4)] EngineerTaskItem[] Tasks,
    [property: Id(5)] string[]? CleanupTargets = null,
    [property: Id(6)] EngineerTaskSlice[]? Slices = null,
    [property: Id(7)] string? CodeGraphDigest = null)
{
    public EngineerTaskPlan() : this("", "", "", [], []) { }

    public RatifiedPlan ToRatifiedPlan(
        EngineeringHarnessKind harness = EngineeringHarnessKind.InoNative,
        bool ratified = true,
        string? ratifiedBy = "auto",
        DateTimeOffset? ratifiedAt = null)
        => new(
            Goal: Goal,
            ArchitectureSummary: ArchitectureSummary,
            CleanupTargets: CleanupTargets ?? [],
            Slices: Slices is { Length: > 0 } ? Slices : EngineerTaskSlices.FromTaskPlan(this),
            Ratified: ratified,
            RatifiedBy: ratifiedBy,
            RatifiedAt: ratifiedAt,
            Harness: harness,
            CodeGraphDigest: CodeGraphDigest);

    public string? Validate(int maxChangedFiles = 5)
    {
        if (string.IsNullOrWhiteSpace(Goal))
        {
            return "EngineerTaskPlan requires a non-empty goal.";
        }

        if ((Tasks is null || Tasks.Length == 0) && (Slices is null || Slices.Length == 0))
        {
            return "EngineerTaskPlan must contain at least one task or slice.";
        }

        var distinctTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawCleanup in CleanupTargets ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawCleanup))
            {
                return "EngineerTaskPlan contains a blank cleanup target path.";
            }

            var clean = rawCleanup.Trim().Replace('\\', '/');
            var hashIdx = clean.IndexOf('#', StringComparison.Ordinal);
            var colonIdx = hashIdx < 0 ? clean.IndexOf("::", StringComparison.Ordinal) : -1;
            var fileOnly = hashIdx > 0 ? clean[..hashIdx] : (colonIdx > 0 ? clean[..colonIdx] : clean);
            if (fileOnly.StartsWith('/') || Path.IsPathRooted(fileOnly) || fileOnly.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
            {
                return $"EngineerTaskPlan contains invalid or hidden cleanup target path '{rawCleanup}'.";
            }

            distinctTargets.Add(fileOnly);
        }

        if (Tasks is { Length: > 0 })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
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

            if (!TryComputeTopologicalOrder(out _))
            {
                return "EngineerTaskPlan contains a dependency cycle.";
            }
        }

        if (Slices is { Length: > 0 })
        {
            foreach (var slice in Slices)
            {
                foreach (var rawOwned in slice?.OwnedPaths ?? [])
                {
                    if (string.IsNullOrWhiteSpace(rawOwned))
                    {
                        return $"Slice '{slice?.SliceId}' contains a blank owned path.";
                    }

                    var clean = rawOwned.Trim().Replace('\\', '/');
                    if (clean.StartsWith('/') || Path.IsPathRooted(clean) || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
                    {
                        return $"Slice '{slice?.SliceId}' contains invalid or hidden owned path '{rawOwned}'.";
                    }

                    distinctTargets.Add(clean);
                }
            }

            var disjointRule = new NoOverlappingFileSlices(Slices);
            if (disjointRule.Broken)
            {
                return $"{disjointRule.Name}: {disjointRule.Message}";
            }
        }

        if (maxChangedFiles > 0 && distinctTargets.Count > maxChangedFiles)
        {
            return $"EngineerTaskPlan targets {distinctTargets.Count} distinct files, exceeding the policy maximum of {maxChangedFiles}.";
        }

        return null;
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

