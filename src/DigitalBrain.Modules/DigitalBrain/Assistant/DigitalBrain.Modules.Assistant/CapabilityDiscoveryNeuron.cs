using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Assistant;

[GrainType("assistant.discovery")]
internal sealed class CapabilityDiscoveryNeuron(RegistryDiscoveryService discovery) : Neuron, ICapabilityDiscovery
{
    public override NeuronAccess Access(string operation)
    {
        RequireScope();
        return NeuronAccess.PublicOperation;
    }

    private RegistryDiscoveryService RequireScope()
    {
        var caller = CallerContextStamper.Require();
        if (this.GetPrimaryKeyString() != BrainScope.Create(caller.AccountId, caller.BrainId).Id)
        { throw new UnauthorizedAccessException("Discovery must use the caller's current brain."); }
        return discovery;
    }

    public Task<RegistryDiscovery> Discover(string query, CancellationToken cancellationToken = default)
        => RequireScope().Discover(query, cancellationToken);
    public Task<RegistryDiscovery> Browse(string query, string? provider = null, int offset = 0, int limit = 10, CancellationToken cancellationToken = default)
        => RequireScope().Browse(query, provider, offset, limit, cancellationToken);
    public Task<RegistryCapability?> Select(string id, CancellationToken cancellationToken = default)
        => RequireScope().Select(id, cancellationToken);
}
