using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.AI.Models;

[GrainType("ai.models"), StatelessWorker]
internal sealed class ModelCatalogNeuron(ModelCatalog catalog) : Neuron, IModelCatalog
{
    public Task<ModelChoices> List(string? defaultModel = null) => catalog.List(defaultModel, CancellationToken.None);
    public Task<ModelSelection?> Select(string? id, string? defaultModel = null) => catalog.Select(id, defaultModel, CancellationToken.None);
}
