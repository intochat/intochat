using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Registry;

[GrainType("registry")]
internal sealed class RegistryNeuron(NeuronTypes types, NeuronTypeSearch search, RegistryDiscoveryService discovery)
    : Neuron, IRegistry
{
    public override DigitalBrain.Framework.Enforcement.NeuronAccess Access(string operation)
        => operation is nameof(IRegistry.Types) or nameof(IRegistry.Search) or nameof(IRegistry.Discover) or nameof(IRegistry.Browse) or nameof(IRegistry.Select)
            ? DigitalBrain.Framework.Enforcement.NeuronAccess.PublicOperation : base.Access(operation);

    public Task<RegistryDiscovery> Discover(string query, CancellationToken cancellationToken = default) => discovery.Discover(query, cancellationToken);
    public Task<RegistryDiscovery> Browse(string query, string? provider = null, int offset = 0, int limit = 10, CancellationToken cancellationToken = default)
        => discovery.Browse(query, provider, offset, limit, cancellationToken);
    public Task<RegistryCapability?> Select(string id, CancellationToken cancellationToken = default) => discovery.Select(id, cancellationToken);

    public Task<IReadOnlyList<NeuronType>> Types() => Task.FromResult(types.Read());

    public Task<IReadOnlyList<NeuronTypeHit>> Search(string query, int take = 10, CancellationToken cancellationToken = default)
        => search.Search(query, take, cancellationToken);

}
