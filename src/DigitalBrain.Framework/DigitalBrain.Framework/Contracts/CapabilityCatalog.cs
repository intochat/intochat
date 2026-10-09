using Microsoft.Extensions.Logging;

namespace DigitalBrain.Framework.Contracts;

// Everything a brain can search and call: the deployed contracts plus the runtime capabilities each
// source reports for one brain. brainId null reads the contracts alone; a source that throws is
// logged and skipped so the contracts stay searchable.
public sealed class CapabilityCatalog(ContractCatalog contracts, IEnumerable<IRuntimeCapabilitySource> sources, ILogger<CapabilityCatalog>? logger = null)
{
    public ContractCatalog Contracts { get; } = contracts;

    public async Task<IReadOnlyList<NeuronType>> Read(string? brainId, CancellationToken ct)
    {
        var entries = new List<NeuronType>(Contracts.Read());
        if (brainId is null) { return entries; }
        foreach (var source in sources)
        {
            try { entries.AddRange(await source.Read(brainId, ct).ConfigureAwait(false)); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger?.LogWarning(error, "Capability source {Source} failed for brain {Brain}", source.GetType().Name, brainId); }
        }
        return entries;
    }
}