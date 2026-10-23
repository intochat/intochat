using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Framework.Platform.Entries;

namespace IntoChat.OS.Authority.Entries;

internal sealed class BrainProviderKeys(IGrainFactory grains) : IBrainProviderKeys
{
    public async Task<string?> Release(string brainId, string providerId, CancellationToken cancellationToken = default)
        => await grains.GetGrain<IBrainEntries>(brainId)
            .Release(IntegrationNeuron.PlatformCaller, BrainEntries.ProviderEntry(providerId))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
}
