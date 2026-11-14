using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[Neuron(Type), StatelessNeuron]
internal sealed class CodeGraphNeuron(CodeGraphService codeGraph) : DigitalBrain.Neurons.Neuron, ICodeGraph
{
    internal const string Type = "roslyn.codegraph";

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    public Task<CodeSymbolSearchResult> SearchSymbols(string repositoryPathOrId, string query, int limit = 20, CancellationToken cancellationToken = default)
        => codeGraph.SearchSymbolsAsync(repositoryPathOrId, query, limit, cancellationToken);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    public Task<CodeFileOutline> FileOutline(string repositoryPathOrId, string relativeFilePath, CancellationToken cancellationToken = default)
        => codeGraph.FileOutlineAsync(repositoryPathOrId, relativeFilePath, cancellationToken);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    public Task<CodeCallersResult> FindCallers(string repositoryPathOrId, string symbolName, int limit = 20, CancellationToken cancellationToken = default)
        => codeGraph.FindCallersAsync(repositoryPathOrId, symbolName, limit, cancellationToken);
}
