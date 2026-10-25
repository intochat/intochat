using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.AI.Models;

[GrainType("ai.models"), StatelessWorker]
internal sealed class ModelCatalogNeuron(ModelCatalog catalog) : Neuron, IModelCatalog
{
    // The brain is the key's scope ("{brain}/default"); a bare key is the deployment's view.
    private string? Brain => BrainKey.ScopeOf(this.GetPrimaryKeyString());

    public Task<ModelChoices> List(string? defaultModel = null) => catalog.List(Brain, defaultModel, CancellationToken.None);
    public Task<ModelSelection?> Select(string? id, string? defaultModel = null) => catalog.Select(Brain, id, defaultModel, CancellationToken.None);
}
