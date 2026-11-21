using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Lightweight fallback <see cref="IWorktreeRoslyn"/> used when the Roslyn module is not registered in-process.
/// </summary>
internal sealed class UnavailableWorktreeRoslyn : IWorktreeRoslyn
{
    public static UnavailableWorktreeRoslyn Instance { get; } = new();

    private UnavailableWorktreeRoslyn()
    {
    }

    public Task<IReadOnlyList<WorktreeSymbolInfo>> InspectSymbolsAsync(
        string worktreeRoot,
        string? relativeFilter = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorktreeSymbolInfo>>([]);

    public Task<IReadOnlyList<WorktreeDiagnostic>> AnalyzeAsync(
        string worktreeRoot,
        IReadOnlyList<string>? relativePaths = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorktreeDiagnostic>>([]);

    public Task<WorktreeEditSummary> ApplyEditsAsync(
        string worktreeRoot,
        IReadOnlyList<WorktreeCSharpEdit> edits,
        CancellationToken ct = default)
        => Task.FromResult(new WorktreeEditSummary(
            Succeeded: false,
            TouchedFiles: [],
            Diagnostics: [],
            Diff: string.Empty,
            Detail: "Roslyn module is not registered in-process."));
}
