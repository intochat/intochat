namespace DigitalBrain.Framework.Contracts;

// Capabilities a brain gains at runtime beyond the deployed contracts (the Tools module's MCP and
// page tool sources). CapabilityCatalog merges every registered source with the ContractCatalog.
public interface IRuntimeCapabilitySource
{
    Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct);
}
