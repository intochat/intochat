using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class EngineerPlanner(
    IServiceProvider? services = null,
    ILogger<EngineerPlanner>? logger = null,
    IWorkTree? workTree = null,
    IWorktreeRoslyn? roslyn = null,
    IWorkingMemory? workingMemory = null) : IEngineerPlanner
{
    public const int MaxCodeGraphDigestBytes = RepositoryCodeGraphDigest.MaxBytes;
    internal const int MaxInteractiveSymbolScanFiles = 200;

    private static readonly TimeSpan InteractiveSymbolScanTimeout = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> ExcludedSymbolScanDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "artifacts",
        "ephemeral",
        "node_modules",
        "TestResults",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DeadOrLegacyMarkers =
    [
        "legacy",
        "obsolete",
        "dead",
        "unused",
        "deprecated",
        "trash",
    ];

    private static readonly string[] CleanupIntentKeywords =
    [
        "delete",
        "remove",
        "clean",
        "cleanup",
        "prune",
        "dead",
        "obsolete",
        "legacy",
        "unused",
        "duplicate",
    ];

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerPlanner>();

    public Task<EngineerTaskPlan> PlanAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct = default)
        => PlanWithContextAsync(
            repositoryId,
            goal,
            allowedExtensions,
            maxChangedFiles,
            brain: null,
            workTreeOverride: null,
            roslynOverride: null,
            workingMemoryOverride: null,
            preHydratedMemory: null,
            repositoryPathOverride: null,
            ct);

    internal async Task<EngineerTaskPlan> PlanWithContextAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        BrainId? brain = null,
        IWorkTree? workTreeOverride = null,
        IWorktreeRoslyn? roslynOverride = null,
        IWorkingMemory? workingMemoryOverride = null,
        EngineerPlannerMemorySnapshot? preHydratedMemory = null,
        string? repositoryPathOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var cleanGoal = goal.Trim();
        var effectiveMax = Math.Max(1, maxChangedFiles);

        var memorySnapshot = preHydratedMemory ?? await HydrateMemoryBriefAsync(
            repositoryId,
            cleanGoal,
            brain,
            workingMemoryOverride,
            ct);

        var (digest, liveSymbols) = await BuildRepositoryCodeGraphDigestAsync(
            repositoryId,
            cleanGoal,
            memorySnapshot,
            workTreeOverride,
            roslynOverride,
            repositoryPathOverride,
            ct);

        if (services?.GetService<IModelInference>() is { } inference)
        {
            var drafted = await TryDraftMultiAgentPlanAsync(
                inference,
                repositoryId,
                cleanGoal,
                allowedExtensions,
                effectiveMax,
                digest,
                memorySnapshot,
                ct);
            if (drafted is not null && drafted.Validate(effectiveMax) is null)
            {
                return drafted;
            }
        }

        return BuildStructuredDefaultPlan(
            repositoryId,
            cleanGoal,
            allowedExtensions,
            effectiveMax,
            digest,
            liveSymbols,
            memorySnapshot);
    }

    internal async Task<EngineerPlannerMemorySnapshot> HydrateMemoryBriefAsync(
        string repositoryId,
        string goal,
        BrainId? brain = null,
        IWorkingMemory? workingMemoryOverride = null,
        CancellationToken ct = default)
    {
        var wm = workingMemoryOverride ?? workingMemory ?? services?.GetService<IWorkingMemory>();
        if (wm is null && brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            wm = proxies.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(b));
        }

        if (wm is null)
        {
            return new EngineerPlannerMemorySnapshot(null, null, null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var pack = await wm.ReadStartupPack();
            var projectNote = string.IsNullOrWhiteSpace(pack.Project)
                ? null
                : InoNativeHarness.CollapseSingleLine(pack.Project, 240);

            var selected = InoNativeHarness.SelectMatchingProjectBrief(pack.AvailableBriefs, repositoryId, goal);
            if (selected is not null)
            {
                var brief = await wm.GetBrief(selected.Key);
                if (brief is not null && !string.IsNullOrWhiteSpace(brief.Content))
                {
                    return new EngineerPlannerMemorySnapshot(
                        brief.Key,
                        InoNativeHarness.CollapseSingleLine(brief.Content, 600),
                        projectNote);
                }
            }
            else if (pack.AvailableBriefs is null or { Length: 0 })
            {
                var repoBrief = await wm.GetBrief($"repo:{repositoryId.Trim()}");
                if (repoBrief is not null && !string.IsNullOrWhiteSpace(repoBrief.Content))
                {
                    return new EngineerPlannerMemorySnapshot(
                        repoBrief.Key,
                        InoNativeHarness.CollapseSingleLine(repoBrief.Content, 600),
                        projectNote);
                }
            }

            return new EngineerPlannerMemorySnapshot(null, null, projectNote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"hydrating IWorkingMemory brief for repository '{repositoryId}'");
            return new EngineerPlannerMemorySnapshot(null, null, null);
        }
    }

}
