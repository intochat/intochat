using System.Collections.Concurrent;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Entries;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.AI;

// Whether a brain holds its own key for a provider, cached per (brain, integration) and revalidated
// against the brain's entry version, so a Put or Remove takes effect on the next check. Only the
// boolean is cached; the key itself is released per client build and never kept.
internal sealed class BrainKeyAvailability(IGrainFactory grains, IAiCredentials credentials)
{
    // The AI module's own Platform stamp for releasing a brain's provider key. ":" cannot occur in a
    // principal id, so no account can ever be this caller (as IntegrationNeuron.PlatformCaller).
    internal static readonly CallerContext PlatformCaller = new()
    {
        PrincipalId = "ai.credentials",
        AccountId = ":ai",
        BrainId = ":ai",
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
        AppId = "ai.credentials",
    };

    private readonly ConcurrentDictionary<(string Brain, string Integration), (bool HasKey, long Version)> _known = new();

    public async Task<(bool HasKey, long Version)> Read(string brainId, AiProvider provider, CancellationToken ct)
    {
        var version = await grains.GetGrain<IBrainEntries>(brainId).Version().WaitAsync(ct).ConfigureAwait(false);
        var slot = (brainId, AiIntegrations.IdOf(provider));
        if (_known.TryGetValue(slot, out var known) && known.Version == version) { return known; }
        var fresh = (await credentials.BrainKey(brainId, provider, ct).ConfigureAwait(false) is { Length: > 0 }, version);
        _known[slot] = fresh;
        return fresh;
    }
}

// The deployment's credentials as one brain sees them: a key-based provider is ready when the brain
// holds its own provider:{id} key, which then wins over the deployment registration. A brain key is
// sent to the provider's public endpoint (or a profile's pinned one), never to an operator-registered
// endpoint, which belongs with the operator's key.
internal sealed class BrainAwareCredentials(IAiCredentials deployment, BrainKeyAvailability keys, string brainId) : IAiCredentials
{
    public async Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
        => Keyed(integrationId) is { } provider && (await keys.Read(brainId, provider, cancellationToken).ConfigureAwait(false)).HasKey
            ? Ready(integrationId)
            : await deployment.StatusOf(integrationId, cancellationToken).ConfigureAwait(false);

    public async Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default)
    {
        var generation = await deployment.GenerationOf(integrationId, cancellationToken).ConfigureAwait(false);
        if (Keyed(integrationId) is not { } provider) { return generation; }
        var (hasKey, version) = await keys.Read(brainId, provider, cancellationToken).ConfigureAwait(false);
        return unchecked((generation * 1_000_003) + (version * 2) + (hasKey ? 1 : 0));
    }

    public async Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        if (Keyed(integrationId) is { } provider && await deployment.BrainKey(brainId, provider, cancellationToken).ConfigureAwait(false) is { Length: > 0 } key)
        { return new ReleasedIntegration { Snapshot = Ready(integrationId), Values = new() { [AiIntegrations.ApiKeyField] = key } }; }
        return await deployment.MaterializeAsync(integrationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default)
        => (await MaterializeAsync(integrationId, cancellationToken).ConfigureAwait(false)).Values[field];

    public Task<string?> BrainKey(string brain, AiProvider provider, CancellationToken cancellationToken = default)
        => deployment.BrainKey(brain, provider, cancellationToken);

    private static AiProvider? Keyed(string integrationId)
        => AiIntegrations.KeyedProviders.Cast<AiProvider?>().FirstOrDefault(provider => AiIntegrations.IdOf(provider!.Value) == integrationId);

    private static IntegrationSnapshot Ready(string integrationId) => new() { IntegrationId = integrationId, Status = IntegrationStatus.Ready };
}
