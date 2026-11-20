using System.Globalization;
using System.Security;
using System.Text;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record InoCleanupResult(
    string[] DeletedFiles,
    string[] ModifiedFiles,
    string[] Summaries);

internal sealed record InoUiStateGraphCheckResult(
    bool Succeeded,
    string[] CheckedTypes,
    string[] Violations);

internal sealed class InoCodeOrchestrator(
    IWorktreeRoslyn? roslyn = null,
    ChangePolicy? policy = null,
    ILogger? logger = null)
{
    private readonly IWorktreeRoslyn _roslyn = roslyn ?? IWorktreeRoslyn.CreateDefault();
    private readonly ChangePolicy _policy = policy ?? ChangePolicy.Default;
    private readonly ILogger _logger = logger ?? OsConsoleLog.For<InoCodeOrchestrator>();

    public IWorktreeRoslyn Roslyn => _roslyn;

    public ChangePolicy Policy => _policy;

    public async Task<InoCleanupResult> ExecuteCleanupTargetsAsync(
        string worktreePath,
        IReadOnlyList<string>? cleanupTargets,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (cleanupTargets is null || cleanupTargets.Count == 0)
        {
            return new InoCleanupResult([], [], []);
        }

        var normalizedRoot = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var deletedFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var summaries = new List<string>();

        foreach (var rawTarget in cleanupTargets)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawTarget))
            {
                continue;
            }

            var trimmed = rawTarget.Trim();
            if (TryParseSymbolCleanupTarget(trimmed, out var filePart, out var typeName, out var memberName))
            {
                var (relPath, fullPath) = _policy.ResolveConfinedPath(normalizedRoot, filePart, requireWriteAllowed: true, allowDirectory: false);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var effectiveType = typeName;
                if (string.IsNullOrWhiteSpace(effectiveType))
                {
                    var symbols = await _roslyn.InspectSymbolsAsync(normalizedRoot, relPath, ct).ConfigureAwait(false);
                    effectiveType = symbols.FirstOrDefault(s => string.Equals(s.FilePath, relPath, StringComparison.OrdinalIgnoreCase))?.Name;
                }

                if (!string.IsNullOrWhiteSpace(effectiveType) && !string.IsNullOrWhiteSpace(memberName))
                {
                    var editSummary = await _roslyn.ApplyEditsAsync(
                        normalizedRoot,
                        [
                            new WorktreeCSharpEdit(
                                Kind: WorktreeCSharpEditKind.RemoveMember,
                                RelativePath: relPath,
                                TypeName: effectiveType,
                                MemberName: memberName),
                        ],
                        ct).ConfigureAwait(false);

                    if (!editSummary.Succeeded)
                    {
                        throw new InvalidOperationException(
                            editSummary.Detail ?? $"Failed to remove dead symbol '{effectiveType}.{memberName}' in '{relPath}'.");
                    }

                    foreach (var touched in editSummary.TouchedFiles)
                    {
                        modifiedFiles.Add(touched);
                    }

                    summaries.Add($"Removed dead symbol {effectiveType}.{memberName} from {relPath}.");
                }

                continue;
            }

            var (confinedRel, confinedFull) = _policy.ResolveConfinedPath(normalizedRoot, trimmed, requireWriteAllowed: true, allowDirectory: false);
            if (File.Exists(confinedFull))
            {
                File.Delete(confinedFull);
                deletedFiles.Add(confinedRel);
                summaries.Add($"Deleted dead file {confinedRel}.");
            }
        }

        return new InoCleanupResult(
            DeletedFiles: [.. deletedFiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ModifiedFiles: [.. modifiedFiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Summaries: [.. summaries]);
    }

    public Task<IReadOnlyList<WorktreeSymbolInfo>> FindSymbolsAsync(
        string worktreePath,
        string? relativeFilter = null,
        CancellationToken ct = default)
        => _roslyn.InspectSymbolsAsync(worktreePath, relativeFilter, ct);

    public Task<WorktreeEditSummary> AddOrReplaceMemberAsync(
        string worktreePath,
        string relativePath,
        string typeName,
        string memberName,
        string memberSource,
        IReadOnlyList<string>? sliceOwnedPaths = null,
        CancellationToken ct = default)
        => ApplyAstEditsAsync(
            worktreePath,
            [
                new WorktreeCSharpEdit(
                    Kind: WorktreeCSharpEditKind.AddOrReplaceMember,
                    RelativePath: relativePath,
                    TypeName: typeName,
                    MemberName: memberName,
                    Source: memberSource),
            ],
            sliceOwnedPaths,
            ct);

    public Task<WorktreeEditSummary> CreateTypeFileAsync(
        string worktreePath,
        string relativePath,
        string namespaceName,
        string typeName,
        string? typeKind,
        string? baseTypes,
        string bodySource,
        IReadOnlyList<string>? sliceOwnedPaths = null,
        CancellationToken ct = default)
        => ApplyAstEditsAsync(
            worktreePath,
            [
                new WorktreeCSharpEdit(
                    Kind: WorktreeCSharpEditKind.CreateTypeFile,
                    RelativePath: relativePath,
                    TypeName: typeName,
                    NamespaceName: namespaceName,
                    Source: bodySource,
                    TypeKind: typeKind,
                    BaseTypes: baseTypes),
            ],
            sliceOwnedPaths,
            ct);

    public async Task<WorktreeEditSummary> ApplyAstEditsAsync(
        string worktreePath,
        IReadOnlyList<WorktreeCSharpEdit> edits,
        IReadOnlyList<string>? sliceOwnedPaths = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(edits);

        var normalizedRoot = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedEdits = new List<WorktreeCSharpEdit>(edits.Count);
        foreach (var edit in edits)
        {
            var (rel, _) = _policy.ResolveConfinedPath(normalizedRoot, edit.RelativePath, requireWriteAllowed: true, allowDirectory: false);
            EnsureWithinSliceOwnership(rel, sliceOwnedPaths);
            normalizedEdits.Add(edit with { RelativePath = rel });
        }

        return await _roslyn.ApplyEditsAsync(normalizedRoot, normalizedEdits, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorktreeDiagnostic>> GetSemanticDiagnosticsAsync(
        string worktreePath,
        IReadOnlyList<string>? relativePaths = null,
        CancellationToken ct = default)
        => _roslyn.AnalyzeAsync(worktreePath, relativePaths, ct);

    public async Task<InoUiStateGraphCheckResult> VerifyModifiedScreensAsync(
        string worktreePath,
        IReadOnlyList<string> touchedFiles,
        IUiIntrospector? uiIntrospector,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(touchedFiles);

        if (uiIntrospector is null)
        {
            return new InoUiStateGraphCheckResult(true, [], []);
        }

        var csFiles = touchedFiles
            .Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (csFiles.Length == 0)
        {
            return new InoUiStateGraphCheckResult(true, [], []);
        }

        var checkedTypes = new List<string>();
        var violations = new List<string>();

        foreach (var relCs in csFiles)
        {
            ct.ThrowIfCancellationRequested();
            var (_, fullPath) = _policy.ResolveConfinedPath(worktreePath, relCs, requireWriteAllowed: false, allowDirectory: false);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var source = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
            if (!source.Contains("AggregateScreen", StringComparison.Ordinal))
            {
                continue;
            }

            var symbols = await _roslyn.InspectSymbolsAsync(worktreePath, relCs, ct).ConfigureAwait(false);
            foreach (var sym in symbols.Where(s => string.Equals(s.FilePath, relCs, StringComparison.OrdinalIgnoreCase)))
            {
                var fullTypeName = string.IsNullOrWhiteSpace(sym.Namespace) ? sym.Name : $"{sym.Namespace}.{sym.Name}";
                if (!IsLoadedAggregateScreenType(fullTypeName))
                {
                    continue;
                }

                try
                {
                    var graph = await uiIntrospector.ExploreStateGraph(fullTypeName, maxStates: 32).ConfigureAwait(false);
                    checkedTypes.Add(fullTypeName);
                    if (!graph.IsHealthy || graph.Violations is { Length: > 0 })
                    {
                        foreach (var violation in graph.Violations ?? [])
                        {
                            violations.Add($"{fullTypeName}: {violation}");
                        }

                        if ((graph.Violations is null || graph.Violations.Length == 0) && !graph.IsHealthy)
                        {
                            violations.Add($"{fullTypeName}: Accordant StateGraph reported unhealthy state.");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"IUiIntrospector StateGraph check skipped for '{fullTypeName}'");
                }
            }
        }

        return new InoUiStateGraphCheckResult(
            Succeeded: violations.Count == 0,
            CheckedTypes: [.. checkedTypes],
            Violations: [.. violations]);
    }

    public static void EnsureWithinSliceOwnership(string normalizedRelativePath, IReadOnlyList<string>? sliceOwnedPaths)
    {
        if (sliceOwnedPaths is null || sliceOwnedPaths.Count == 0)
        {
            return;
        }

        var cleanTarget = EngineerTaskSlices.NormalizePath(normalizedRelativePath);
        foreach (var rawAllowed in sliceOwnedPaths)
        {
            var allowed = EngineerTaskSlices.NormalizePath(rawAllowed);
            if (allowed.Length == 0)
            {
                continue;
            }

            if (string.Equals(cleanTarget, allowed, StringComparison.OrdinalIgnoreCase)
                || cleanTarget.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new SecurityException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Slice file-ownership violation: '{cleanTarget}' is outside this slice's OwnedPaths [{string.Join(", ", sliceOwnedPaths)}]."));
    }

    public static async Task EmitProgressAsync(
        EngineeringHarnessRequest request,
        string sliceId,
        int percent,
        string phase,
        string detail,
        List<OperationProgressed> sink,
        Lock sinkGate,
        Func<OperationProgressed, Task>? onProgress)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        var nodeId = string.IsNullOrWhiteSpace(sliceId) || string.Equals(sliceId, request.WorktreeId, StringComparison.Ordinal)
            ? request.WorktreeId
            : $"{request.WorktreeId}:{sliceId}";
        var signal = new OperationProgressed(
            FlowId: request.FlowId,
            WorktreeId: request.WorktreeId,
            NodeId: nodeId,
            SliceId: sliceId,
            Harness: request.Harness,
            Percent: clampedPercent,
            Phase: phase,
            Detail: detail);

        lock (sinkGate)
        {
            sink.Add(signal);
        }

        if (onProgress is not null)
        {
            await onProgress(signal).ConfigureAwait(false);
        }
    }

    private static bool IsLoadedAggregateScreenType(string fullTypeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? candidate;
            try
            {
                candidate = asm.GetType(fullTypeName, throwOnError: false, ignoreCase: false);
            }
            catch (Exception ex) when (ex is FileLoadException or TypeLoadException or BadImageFormatException)
            {
                OsConsoleLog.For<InoCodeOrchestrator>().LogDebug(ex, "Skipping unloaded assembly type lookup for '{TypeName}'.", fullTypeName);
                continue;
            }

            if (candidate is null)
            {
                continue;
            }

            for (var cur = candidate.BaseType; cur is not null; cur = cur.BaseType)
            {
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(AggregateScreenBase<>))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseSymbolCleanupTarget(
        string rawTarget,
        out string filePath,
        out string? typeName,
        out string? memberName)
    {
        filePath = string.Empty;
        typeName = null;
        memberName = null;

        var hashIdx = rawTarget.IndexOf('#', StringComparison.Ordinal);
        var colonIdx = hashIdx < 0 ? rawTarget.IndexOf("::", StringComparison.Ordinal) : -1;
        if (hashIdx <= 0 && colonIdx <= 0)
        {
            return false;
        }

        var splitPos = hashIdx > 0 ? hashIdx : colonIdx;
        var sepLen = hashIdx > 0 ? 1 : 2;
        filePath = rawTarget[..splitPos].Trim();
        var symbolPart = rawTarget[(splitPos + sepLen)..].Trim();
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(symbolPart))
        {
            return false;
        }

        var dotIdx = symbolPart.LastIndexOf('.');
        if (dotIdx > 0 && dotIdx + 1 < symbolPart.Length)
        {
            typeName = symbolPart[..dotIdx].Trim();
            memberName = symbolPart[(dotIdx + 1)..].Trim();
        }
        else
        {
            memberName = symbolPart;
        }

        return true;
    }
}
