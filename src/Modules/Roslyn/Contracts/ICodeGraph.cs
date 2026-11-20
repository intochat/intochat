using System.ComponentModel;
using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[NeuronAlias("roslyn.codegraph"), DefaultNeuronType("roslyn.codegraph")]
[Public]
[Description("Fast read-only Roslyn C# symbol search, file outline, and call-graph index over allowlisted repositories.")]
public interface ICodeGraph
{
    const string DefaultKey = "roslyn/codegraph";

    static string Key(string brainId) => $"{brainId}/roslyn/codegraph";

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<CodeSymbolSearchResult> SearchSymbols(string repositoryPathOrId, string query, int limit = 20, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<CodeFileOutline> FileOutline(string repositoryPathOrId, string relativeFilePath, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<CodeCallersResult> FindCallers(string repositoryPathOrId, string symbolName, int limit = 20, CancellationToken cancellationToken = default);
}

public sealed record CodeSymbolNode(
    string Name,
    string Kind,
    string Container,
    string Signature,
    string RelativePath,
    int Line,
    int EndLine);

public sealed record CodeSymbolSearchResult(
    string Repository,
    IReadOnlyList<CodeSymbolNode> Symbols,
    int TotalCount,
    bool Truncated);

public sealed record CodeOutlineMember(
    string Name,
    string Kind,
    string Signature,
    int Line,
    int EndLine,
    int Depth);

public sealed record CodeFileOutline(
    string Repository,
    string RelativePath,
    string? Namespace,
    IReadOnlyList<CodeOutlineMember> Members);

public sealed record CodeCallerNode(
    string CallerSymbol,
    string CallerKind,
    string CallerSignature,
    string CalleeExpression,
    string RelativePath,
    int Line);

public sealed record CodeCallersResult(
    string Repository,
    string SymbolName,
    IReadOnlyList<CodeCallerNode> Callers,
    int TotalCount,
    bool Truncated);
