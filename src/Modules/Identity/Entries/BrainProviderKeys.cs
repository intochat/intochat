using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using Orleans;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Contracts.Entries;

namespace IntoChat.OS.Modules.Identity.Entries;

internal sealed class BrainProviderKeys(IGrainFactory grains, EdgeAuthority edge) : IBrainProviderKeys
{
    public async Task<string?> Release(string brainId, string providerId, CancellationToken cancellationToken = default)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("A brain ID is required.", nameof(brainId)); }
        if (!BrainEntries.KeyedProviderIds.Contains(providerId, StringComparer.Ordinal)) { throw new ArgumentException("Not a key-based provider.", nameof(providerId)); }
        return await edge.AsKernel(new BrainId(brainId), () => grains.GetGrain<IBrainEntries>(brainId)
            .Release(BrainEntries.ProviderEntry(providerId))
            .WaitAsync(cancellationToken)).ConfigureAwait(false);
    }
}
