namespace IntoChat.OS.Modules.Microsoft.Roslyn;

public enum WorktreeCSharpEditKind
{
    AddUsing = 0,
    AddOrReplaceMember = 1,
    RemoveMember = 2,
    CreateTypeFile = 3,
}

public sealed record WorktreeSymbolInfo(
    string Name,
    string Namespace,
    string Kind,
    string FilePath,
    int Line,
    string[] Members);

public sealed record WorktreeDiagnostic(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int Line);

public sealed record WorktreeCSharpEdit(
    WorktreeCSharpEditKind Kind,
    string RelativePath,
    string? TypeName = null,
    string? MemberName = null,
    string? NamespaceName = null,
    string? Source = null,
    string? TypeKind = null,
    string? BaseTypes = null);

public sealed record WorktreeEditSummary(
    bool Succeeded,
    string[] TouchedFiles,
    WorktreeDiagnostic[] Diagnostics,
    string Diff,
    string? Detail = null);

public interface IWorktreeRoslyn
{
    Task<IReadOnlyList<WorktreeSymbolInfo>> InspectSymbolsAsync(
        string worktreeRoot,
        string? relativeFilter = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorktreeDiagnostic>> AnalyzeAsync(
        string worktreeRoot,
        IReadOnlyList<string>? relativePaths = null,
        CancellationToken ct = default);

    Task<WorktreeEditSummary> ApplyEditsAsync(
        string worktreeRoot,
        IReadOnlyList<WorktreeCSharpEdit> edits,
        CancellationToken ct = default);
}
