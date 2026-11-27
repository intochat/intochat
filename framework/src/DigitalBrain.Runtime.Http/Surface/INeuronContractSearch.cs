using DigitalBrain.Capabilities.Connections.Integrations;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Searches available neuron contracts and live capabilities visible to the current caller.
/// </summary>
public interface INeuronContractSearch
{
    /// <summary>Reads all searchable neuron contracts and live capability entries for the current caller.</summary>
    Task<IReadOnlyList<NeuronType>> Capabilities(CancellationToken ct);

    /// <summary>Ranks <paramref name="entries"/> against <paramref name="query"/>.</summary>
    Task<IReadOnlyList<NeuronTypeHit>> Search(
        string query,
        IReadOnlyList<NeuronType> entries,
        int take = 10,
        CancellationToken ct = default);
}
