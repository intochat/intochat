using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans;

namespace IntoChat.OS.Authority.Entries;

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
