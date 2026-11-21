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

internal sealed partial class EngineerPlanner
{
    internal async Task<(RepositoryCodeGraphDigest Digest, IReadOnlyList<WorktreeSymbolInfo> Symbols)> BuildRepositoryCodeGraphDigestAsync(
        string repositoryId,
        string goal,
        EngineerPlannerMemorySnapshot memory,
        IWorkTree? workTreeOverride = null,
        IWorktreeRoslyn? roslynOverride = null,
        string? repositoryPathOverride = null,
        CancellationToken ct = default)
    {
        var effectiveWorkTree = workTreeOverride ?? workTree ?? services?.GetService<IWorkTree>();
        var explicitRoslyn = roslynOverride ?? roslyn ?? services?.GetService<IWorktreeRoslyn>();

        string? resolvedRepoPath = null;
        if (!string.IsNullOrWhiteSpace(repositoryPathOverride) && Directory.Exists(repositoryPathOverride))
        {
            resolvedRepoPath = Path.GetFullPath(repositoryPathOverride);
        }
        else if (effectiveWorkTree is not null)
        {
            try
            {
                if (RepositoryId.TryParse(repositoryId, out var parsedRepoId) && parsedRepoId is not null)
                {
                    var registered = effectiveWorkTree.ResolveRepository(parsedRepoId);
                    if (!string.IsNullOrWhiteSpace(registered.LocalPath) && Directory.Exists(registered.LocalPath))
                    {
                        resolvedRepoPath = Path.GetFullPath(registered.LocalPath);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Repository '{RepositoryId}' is not registered in IWorkTree during pre-plan CodeGraph hydration.", repositoryId);
            }
        }
        else if (Directory.Exists(repositoryId))
        {
            resolvedRepoPath = Path.GetFullPath(repositoryId);
        }

        IReadOnlyList<WorktreeSymbolInfo> symbols = [];
        if (!string.IsNullOrWhiteSpace(resolvedRepoPath))
        {
            if (!ExceedsInteractiveSymbolScanCap(resolvedRepoPath, MaxInteractiveSymbolScanFiles))
            {
                try
                {
                    using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    scanCts.CancelAfter(InteractiveSymbolScanTimeout);
                    var activeRoslyn = explicitRoslyn ?? UnavailableWorktreeRoslyn.Instance;
                    symbols = await activeRoslyn.InspectSymbolsAsync(resolvedRepoPath, relativeFilter: null, scanCts.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Timed out inspecting C# symbols in '{RepoPath}' for EngineerPlanner; continuing with bounded context.", resolvedRepoPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"inspecting C# symbols in '{resolvedRepoPath}' for EngineerPlanner");
                }
            }
            else
            {
                _logger.LogDebug(
                    "Skipping full C# symbol scan in '{RepoPath}' because repository exceeds {MaxFiles} C# files.",
                    resolvedRepoPath,
                    MaxInteractiveSymbolScanFiles);
            }
        }
        else if (explicitRoslyn is not null)
        {
            try
            {
                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                scanCts.CancelAfter(InteractiveSymbolScanTimeout);
                symbols = await explicitRoslyn.InspectSymbolsAsync(repositoryId, relativeFilter: null, scanCts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Timed out inspecting C# symbols for '{RepositoryId}' via explicit IWorktreeRoslyn.", repositoryId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skipping IWorktreeRoslyn symbol inspection for unrooted repository '{RepositoryId}'.", repositoryId);
            }
        }

        if (symbols.Count == 0
            && string.IsNullOrWhiteSpace(memory.BriefContent)
            && string.IsNullOrWhiteSpace(memory.ProjectNote))
        {
            return (new RepositoryCodeGraphDigest(repositoryId, string.Empty, 0), []);
        }

        var groundedFiles = symbols
            .Select(static s => EngineerTaskSlices.NormalizePath(s.FilePath))
            .Where(static p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var potentialCleanup = DetectPotentialCleanupTargets(symbols);
        var duplicateSymbols = DetectDuplicateSymbols(symbols);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"REPO: {repositoryId} ({symbols.Count} C# symbol(s) across {groundedFiles.Length} file(s))"));

        if (!string.IsNullOrWhiteSpace(memory.ProjectNote))
        {
            sb.AppendLine($"PROJECT: {memory.ProjectNote}");
        }

        if (!string.IsNullOrWhiteSpace(memory.BriefKey) && !string.IsNullOrWhiteSpace(memory.BriefContent))
        {
            sb.AppendLine($"BRIEF[{memory.BriefKey}]: {memory.BriefContent}");
        }

        if (potentialCleanup.Length > 0)
        {
            sb.AppendLine("CLEANUP_CANDIDATES: " + string.Join(", ", potentialCleanup.Take(8)));
        }

        if (duplicateSymbols.Length > 0)
        {
            sb.AppendLine("DUPLICATE_SYMBOLS: " + string.Join(" | ", duplicateSymbols.Take(6)));
        }

        if (symbols.Count > 0)
        {
            sb.AppendLine("CODEGRAPH_OUTLINES:");
            var rankedSymbols = RankSymbolsForGoal(symbols, goal, memory.BriefContent);
            foreach (var sym in rankedSymbols.Take(18))
            {
                var normPath = EngineerTaskSlices.NormalizePath(sym.FilePath);
                var qualified = string.IsNullOrWhiteSpace(sym.Namespace) ? sym.Name : $"{sym.Namespace}.{sym.Name}";
                var memberList = sym.Members is { Length: > 0 }
                    ? $" [{string.Join(", ", sym.Members.Take(6))}]"
                    : string.Empty;
                sb.AppendLine($"- {normPath} :: {sym.Kind} {qualified}{memberList}");
            }
        }

        var compact = InoNativeHarness.TruncateUtf8(sb.ToString().Trim(), MaxCodeGraphDigestBytes);
        var byteCount = Encoding.UTF8.GetByteCount(compact);

        var digest = new RepositoryCodeGraphDigest(
            RepositoryId: repositoryId,
            CompactDigest: compact,
            ByteCount: byteCount,
            MatchedBriefKey: memory.BriefKey,
            GroundedFiles: groundedFiles,
            PotentialCleanupTargets: potentialCleanup,
            DuplicateSymbols: duplicateSymbols);

        return (digest, symbols);
    }


    private static string[] DetectPotentialCleanupTargets(IReadOnlyList<WorktreeSymbolInfo> symbols)
    {
        if (symbols.Count == 0)
        {
            return [];
        }

        var targets = new List<string>();
        var byFile = symbols
            .GroupBy(static s => EngineerTaskSlices.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static g => g.Key, StringComparer.Ordinal);

        foreach (var fileGroup in byFile)
        {
            var relPath = fileGroup.Key;
            if (relPath.Length == 0)
            {
                continue;
            }

            var fileStem = Path.GetFileNameWithoutExtension(relPath);
            var fileTypes = fileGroup.ToArray();
            if (IsDeadOrLegacyIdentifier(fileStem) || fileTypes.All(static s => IsDeadOrLegacyIdentifier(s.Name)))
            {
                targets.Add(relPath);
                continue;
            }

            foreach (var sym in fileTypes)
            {
                foreach (var member in sym.Members ?? [])
                {
                    if (IsDeadOrLegacyIdentifier(member))
                    {
                        targets.Add($"{relPath}#{sym.Name}.{member}");
                    }
                }
            }
        }

        return [.. targets.Distinct(StringComparer.Ordinal)];
    }

    private static string[] DetectDuplicateSymbols(IReadOnlyList<WorktreeSymbolInfo> symbols)
    {
        if (symbols.Count < 2)
        {
            return [];
        }

        var duplicates = new List<string>();
        foreach (var group in symbols.GroupBy(static s => s.Name, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var distinctFiles = group
                .Select(static s => EngineerTaskSlices.NormalizePath(s.FilePath))
                .Where(static p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (distinctFiles.Length > 1)
            {
                duplicates.Add($"{group.Key} ({string.Join(", ", distinctFiles.Take(3))})");
            }
        }

        return [.. duplicates];
    }

    private static IReadOnlyList<WorktreeSymbolInfo> RankSymbolsForGoal(
        IReadOnlyList<WorktreeSymbolInfo> symbols,
        string goal,
        string? briefContent)
    {
        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        return
        [
            .. symbols
                .OrderByDescending(sym => ScoreSymbolRelevance(sym, goal, combined))
                .ThenBy(static sym => sym.FilePath, StringComparer.Ordinal)
                .ThenBy(static sym => sym.Line),
        ];
    }

    private static int ScoreSymbolRelevance(WorktreeSymbolInfo sym, string goal, string combined)
    {
        var score = 0;
        var fileStem = Path.GetFileNameWithoutExtension(sym.FilePath);
        if (goal.Contains(sym.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        else if (combined.Contains(sym.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 55;
        }

        if (!string.IsNullOrWhiteSpace(fileStem) && goal.Contains(fileStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 75;
        }
        else if (!string.IsNullOrWhiteSpace(fileStem) && combined.Contains(fileStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        foreach (var member in sym.Members ?? [])
        {
            if (member.Length >= 3 && combined.Contains(member, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
                break;
            }
        }

        if (IsDeadOrLegacyIdentifier(sym.Name) || IsDeadOrLegacyIdentifier(fileStem))
        {
            score += 20;
        }

        return score;
    }

    private static string[] SelectGroundedCleanupTargets(
        IReadOnlyList<string> candidates,
        string goal,
        string? briefContent,
        int maxBudget,
        IReadOnlySet<string>? excludeWholeFiles)
    {
        if (candidates.Count == 0 || maxBudget <= 0)
        {
            return [];
        }

        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        var explicitCleanupIntent = CleanupIntentKeywords.Any(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var chosen = new List<string>();

        foreach (var candidate in candidates)
        {
            if (chosen.Count >= maxBudget)
            {
                break;
            }

            var hashPos = candidate.IndexOf('#', StringComparison.Ordinal);
            var filePart = hashPos > 0 ? candidate[..hashPos] : candidate;
            if (hashPos < 0 && excludeWholeFiles is not null && excludeWholeFiles.Contains(filePart))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(filePart);
            var memberPart = hashPos > 0 ? candidate[(hashPos + 1)..] : string.Empty;
            var lastDot = memberPart.LastIndexOf('.');
            var leafSymbol = lastDot >= 0 ? memberPart[(lastDot + 1)..] : stem;

            var namedInContext = (!string.IsNullOrWhiteSpace(stem) && combined.Contains(stem, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(leafSymbol) && combined.Contains(leafSymbol, StringComparison.OrdinalIgnoreCase));

            if (namedInContext || explicitCleanupIntent)
            {
                chosen.Add(candidate);
            }
        }

        return [.. chosen];
    }

    private static string[] SelectGroundedSliceFiles(
        IReadOnlyList<WorktreeSymbolInfo> symbols,
        string goal,
        string? briefContent,
        IReadOnlySet<string> deletedWholeFiles,
        int maxFiles)
    {
        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        var candidates = symbols
            .GroupBy(static s => EngineerTaskSlices.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !deletedWholeFiles.Contains(g.Key))
            .Select(g => (
                FilePath: g.Key,
                Score: g.Max(sym => ScoreSymbolRelevance(sym, goal, combined))))
            .OrderByDescending(static x => x.Score)
            .ThenBy(static x => x.FilePath, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var matched = candidates
            .Where(static c => c.Score > 0)
            .Take(maxFiles)
            .Select(static c => c.FilePath)
            .ToArray();

        return matched.Length > 0
            ? matched
            : [.. candidates.Take(Math.Min(maxFiles, 1)).Select(static c => c.FilePath)];
    }

    private static EngineerTaskSlice[] EnsureDisjointSlices(IReadOnlyList<EngineerTaskSlice> slices)
    {
        if (slices.Count <= 1 || !new NoOverlappingFileSlices(slices).Broken)
        {
            return [.. slices];
        }

        var adjusted = new EngineerTaskSlice[slices.Count];
        var lastOwnerByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < slices.Count; i++)
        {
            var cur = slices[i];
            var deps = new HashSet<string>(cur.DependsOnSliceIds ?? [], StringComparer.Ordinal);
            foreach (var rawPath in cur.OwnedPaths ?? [])
            {
                var norm = EngineerTaskSlices.NormalizePath(rawPath);
                if (norm.Length == 0)
                {
                    continue;
                }

                foreach (var (seenPath, ownerSliceId) in lastOwnerByPath)
                {
                    if (!string.Equals(ownerSliceId, cur.SliceId, StringComparison.Ordinal)
                        && (string.Equals(seenPath, norm, StringComparison.OrdinalIgnoreCase)
                            || seenPath.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase)
                            || norm.StartsWith(seenPath + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        deps.Add(ownerSliceId);
                    }
                }

                lastOwnerByPath[norm] = cur.SliceId;
            }

            adjusted[i] = cur with { DependsOnSliceIds = [.. deps.Order(StringComparer.Ordinal)] };
        }

        return adjusted;
    }

    private static bool IsDeadOrLegacyIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var marker in DeadOrLegacyMarkers)
        {
            if (identifier.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeSliceToken(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.Length > 0 ? (sb.Length <= 16 ? sb.ToString() : sb.ToString(0, 16)) : "unit";
    }

    private bool ExceedsInteractiveSymbolScanCap(string rootPath, int maxCSharpFiles)
    {
        var csFileCount = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var currentDir = pending.Pop();
            try
            {
                using (var files = Directory.EnumerateFiles(currentDir, "*.cs", SearchOption.TopDirectoryOnly).GetEnumerator())
                {
                    while (files.MoveNext())
                    {
                        csFileCount++;
                        if (csFileCount > maxCSharpFiles)
                        {
                            return true;
                        }
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(dirName)
                        || dirName.StartsWith('.')
                        || ExcludedSymbolScanDirectories.Contains(dirName))
                    {
                        continue;
                    }

                    pending.Push(subDir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping inaccessible directory '{Directory}' while counting C# files for EngineerPlanner.", currentDir);
            }
        }

        return false;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "...";
}
