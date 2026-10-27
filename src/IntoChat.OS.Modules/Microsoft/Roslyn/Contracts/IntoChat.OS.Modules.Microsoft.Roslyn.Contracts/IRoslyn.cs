using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[NeuronAlias("microsoft.roslyn"), DefaultNeuronType("microsoft.roslyn")]
public interface IRoslyn
{
    Task<WorkspaceReceipt> Open(OpenWorkspace request);

    Task<WorkspaceReceipt> Reload();

    [DigitalBrain.ReadOnlyCall]
    Task<WorkspaceSnapshot> Read();

    [DigitalBrain.ReadOnlyCall]
    Task<SymbolSearchResult> FindSymbols(SymbolSearch query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<ReferenceSearchResult> References(ReferenceSearch query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<DiagnosticsResult> Diagnostics(DiagnosticsQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<SolutionMap> Map(MapQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<Skeleton> Skeleton(SkeletonQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<MemberSource> Member(MemberQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<CallersResult> Callers(CallersQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<SymbolSearchResult> Implementations(ImplementationsQuery query, CancellationToken cancellationToken = default);

    [DigitalBrain.ReadOnlyCall]
    Task<SymbolSearchResult> Derived(DerivedQuery query, CancellationToken cancellationToken = default);

    Task<EditCheck> CheckEdits(IReadOnlyList<EditRequest> edits, CancellationToken cancellationToken = default);

    Task<EditCommit> CommitEdits(IReadOnlyList<EditRequest> edits, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("microsoft.roslyn.edit-check")]
public sealed record EditCheck(
    [property: Id(0)] IReadOnlyList<DiagnosticHit> Diagnostics,
    [property: Id(1)] string Diff,
    [property: Id(2)] int? FailingEdit,
    [property: Id(3)] string? Detail,
    [property: Id(4)] bool HasErrors);

[GenerateSerializer, Alias("microsoft.roslyn.edit-commit")]
public sealed record EditCommit(
    [property: Id(0)] IReadOnlyList<DiagnosticHit> Diagnostics,
    [property: Id(1)] string Diff,
    [property: Id(2)] IReadOnlyList<string> WrittenPaths,
    [property: Id(3)] long SnapshotVersion,
    [property: Id(4)] string? Detail,
    [property: Id(5)] bool HasErrors);
