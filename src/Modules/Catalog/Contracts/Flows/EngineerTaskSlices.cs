using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.task-slice")]
public sealed record EngineerTaskSlice(
    [property: Id(0)] string SliceId,
    [property: Id(1)] string Title,
    [property: Id(2)] string[] OwnedPaths,
    [property: Id(3)] string[] DependsOnSliceIds,
    [property: Id(4)] string Status = "pending",
    [property: Id(5)] int Percent = 0,
    [property: Id(6)] string AssignedAgentKey = "ino.slice-1",
    [property: Id(7)] string Language = "csharp",
    [property: Id(8)] string Instructions = "",
    [property: Id(9)] int SelfRepairAttempts = 0)
{
    public EngineerTaskSlice()
        : this("", "", [], [], "pending", 0, "ino.slice-1", "csharp", "", 0)
    {
    }
}



public sealed record NoOverlappingFileSlices(IReadOnlyList<EngineerTaskSlice> Slices) : Rule("overlapping_file_slices")
{
    public override bool Broken => EvaluateViolation(Slices) is not null;

    public override string Message => EvaluateViolation(Slices) ?? "Parallel DAG wave slices have disjoint file ownership.";

    internal static string? EvaluateViolation(IReadOnlyList<EngineerTaskSlice>? slices)
    {
        if (slices is null || slices.Count == 0)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            if (slice is null || string.IsNullOrWhiteSpace(slice.SliceId))
            {
                return "Every EngineerTaskSlice must have a non-empty SliceId.";
            }

            if (!ids.Add(slice.SliceId.Trim()))
            {
                return $"Duplicate SliceId '{slice.SliceId}' in slice plan.";
            }
        }

        if (!EngineerTaskSlices.TryComputeWaves(slices, out var waves, out var dagError))
        {
            return dagError ?? "Slice dependency graph contains a cycle or unknown dependency.";
        }

        for (var waveIndex = 0; waveIndex < waves.Length; waveIndex++)
        {
            var wave = waves[waveIndex];
            if (wave.Length < 2)
            {
                continue;
            }

            var claimed = new List<(string SliceId, string NormalizedPath)>();
            foreach (var slice in wave)
            {
                var slicePaths = (slice.OwnedPaths ?? [])
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .Select(EngineerTaskSlices.NormalizePath)
                    .Where(static p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var candidatePath in slicePaths)
                {
                    foreach (var (priorSliceId, priorPath) in claimed)
                    {
                        if (string.Equals(priorSliceId, slice.SliceId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (PathsOverlap(priorPath, candidatePath))
                        {
                            return string.Create(
                                CultureInfo.InvariantCulture,
                                $"Parallel slices '{priorSliceId}' and '{slice.SliceId}' in DAG wave {waveIndex + 1} claim overlapping OwnedPaths ('{priorPath}' vs '{candidatePath}').");
                        }
                    }

                    claimed.Add((slice.SliceId, candidatePath));
                }
            }
        }

        return null;
    }

    private static bool PathsOverlap(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
}

public static class EngineerTaskSlices
{
    public static NoOverlappingFileSlices CheckDisjoint(IReadOnlyList<EngineerTaskSlice> slices)
        => new(slices);

    public static string NormalizePath(string rawPath)
        => (rawPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

    public static EngineerTaskSlice[][] ComputeWaves(IReadOnlyList<EngineerTaskSlice> slices)
        => TryComputeWaves(slices, out var waves, out var error)
            ? waves
            : throw new InvalidOperationException(error ?? "Cannot compute topological waves for invalid EngineerTaskSlice DAG.");

    public static bool TryComputeWaves(
        IReadOnlyList<EngineerTaskSlice>? slices,
        out EngineerTaskSlice[][] waves,
        out string? error)
    {
        waves = [];
        error = null;
        if (slices is null || slices.Count == 0)
        {
            return true;
        }

        var byId = new Dictionary<string, EngineerTaskSlice>(slices.Count, StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            if (slice is null || string.IsNullOrWhiteSpace(slice.SliceId))
            {
                error = "Every EngineerTaskSlice must declare a non-empty SliceId.";
                return false;
            }

            var id = slice.SliceId.Trim();
            if (!byId.TryAdd(id, slice))
            {
                error = $"Duplicate SliceId '{id}' in slice plan.";
                return false;
            }
        }

        var remainingDeps = new Dictionary<string, HashSet<string>>(slices.Count, StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            var id = slice.SliceId.Trim();
            var deps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawDep in slice.DependsOnSliceIds ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawDep))
                {
                    continue;
                }

                var dep = rawDep.Trim();
                if (string.Equals(dep, id, StringComparison.Ordinal))
                {
                    error = $"Slice '{id}' cannot depend on itself.";
                    return false;
                }

                if (!byId.ContainsKey(dep))
                {
                    error = $"Slice '{id}' depends on unknown slice '{dep}'.";
                    return false;
                }

                deps.Add(dep);
            }

            remainingDeps[id] = deps;
        }

        var waveList = new List<EngineerTaskSlice[]>();
        var unscheduled = slices.Select(static s => s.SliceId.Trim()).ToList();

        while (unscheduled.Count > 0)
        {
            var readyIds = unscheduled
                .Where(id => remainingDeps[id].Count == 0)
                .ToArray();

            if (readyIds.Length == 0)
            {
                error = "Slice plan contains a dependency cycle.";
                return false;
            }

            waveList.Add([.. readyIds.Select(id => byId[id])]);
            foreach (var completedId in readyIds)
            {
                unscheduled.Remove(completedId);
            }

            foreach (var pendingId in unscheduled)
            {
                foreach (var completedId in readyIds)
                {
                    remainingDeps[pendingId].Remove(completedId);
                }
            }
        }

        waves = [.. waveList];
        return true;
    }

    public static EngineerTaskSlice[] FromTaskPlan(EngineerTaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Slices is { Length: > 0 })
        {
            return plan.Slices;
        }

        if (plan.Tasks is null || plan.Tasks.Length == 0)
        {
            return [];
        }

        var converted = new EngineerTaskSlice[plan.Tasks.Length];
        for (var i = 0; i < plan.Tasks.Length; i++)
        {
            var task = plan.Tasks[i];
            converted[i] = new EngineerTaskSlice(
                SliceId: task.Id,
                Title: task.Title,
                OwnedPaths: task.TargetFiles ?? [],
                DependsOnSliceIds: task.DependsOn ?? [],
                Status: "pending",
                Percent: 0,
                AssignedAgentKey: string.Create(CultureInfo.InvariantCulture, $"ino.slice-{i + 1}"),
                Language: string.IsNullOrWhiteSpace(task.Language) ? "csharp" : task.Language,
                Instructions: task.Instructions ?? string.Empty);
        }

        return converted;
    }
}

