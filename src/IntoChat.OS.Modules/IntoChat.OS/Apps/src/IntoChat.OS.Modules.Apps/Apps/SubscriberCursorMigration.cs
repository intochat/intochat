using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Storage;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Side-by-side dual-reader fallback resolver and cutover watermark enforcer for subscriber cursors during Release N.
/// Resolves the semantic sequence impedance mismatch between publisher-relative legacy cursors and the unified brain-wide log.
/// </summary>
public static class SubscriberCursorMigration
{
    /// <summary>
    /// Resolves the effective signal cursor for a subscriber app using a 4-tier fallback strategy:
    /// 1. Primary: AppState.SignalCursor > 0 (already migrated) -> return immediately without touching legacy storage.
    /// 2. Fallback A: Legacy "synapses" storage -> if publisher has Cursor > 0, seed SignalCursor = LatestPosition, persist atomically.
    /// 3. Fallback B: Legacy ISynapseCursorRegistry -> if subscriber/publisher has Cursor > 0, seed SignalCursor = LatestPosition, persist atomically.
    /// 4. Default: Brand-new apps default to 0.
    /// </summary>
    public static async Task<long> ResolveSubscriberCursorAsync(
        string brain,
        string subscriberId,
        NeuronId publisherId,
        AppState currentState,
        Func<AppState, Task> persistState,
        IBrainSignalLog signalLog,
        IGrainFactory? grainFactory = null,
        IGrainStorage? clusterStorage = null,
        ILogger? logger = null,
        CancellationToken ct = default,
        ISynapseCursorRegistry? legacyRegistryOverride = null,
        SynapseState? legacySynapseStateOverride = null,
        Func<string, ISynapseCursorRegistry>? legacyRegistryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(persistState);
        ArgumentNullException.ThrowIfNull(signalLog);

        // 1. Primary Path: Check subscriber's own persistent state
        if (currentState.SignalCursor > 0)
        {
            return currentState.SignalCursor;
        }

        // 2. Migration Fallback Path A: Check subscriber's legacy "synapses" storage
        try
        {
            var publisherKey = publisherId.ToString();
            SynapseState? synapseState = legacySynapseStateOverride;

            if (synapseState is null && clusterStorage is not null)
            {
                var grainState = new GrainState<SynapseState>();
                var grainId = GrainId.TryParse(subscriberId, out var parsedId)
                    ? parsedId
                    : GrainId.Create("neuron.host", subscriberId);

                await clusterStorage.ReadStateAsync(SynapseState.Name, grainId, grainState).ConfigureAwait(false);
                synapseState = grainState.State;
            }

            var legacySource = synapseState?.Sources?
                .FirstOrDefault(s => string.Equals(s.Source, publisherKey, StringComparison.Ordinal));

            if (legacySource is not null && legacySource.Cursor > 0)
            {
                // Enforce Cutover Watermark Invariant: seed to latest log position to prevent historical replay storm
                var cutoverPosition = await signalLog.GetLatestPositionAsync(ct).ConfigureAwait(false);
                currentState.SignalCursor = cutoverPosition;
                await persistState(currentState).ConfigureAwait(false);
                return cutoverPosition;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read legacy SynapseState for subscriber {Subscriber}.", subscriberId);
        }

        // 3. Migration Fallback Path B: Query legacy registry via ISynapseCursorRegistry.GetCursor
        try
        {
            var legacyRegistry = legacyRegistryOverride
                ?? legacyRegistryFactory?.Invoke(brain)
                ?? (grainFactory as ISynapseCursorRegistry);

            if (legacyRegistry is not null)
            {
                var legacyCursor = await legacyRegistry.GetCursor(subscriberId, publisherId).ConfigureAwait(false);
                if (legacyCursor.HasValue && legacyCursor.Value > 0)
                {
                    // Enforce Cutover Watermark Invariant: seed to latest log position to prevent historical replay storm
                    var cutoverPosition = await signalLog.GetLatestPositionAsync(ct).ConfigureAwait(false);
                    currentState.SignalCursor = cutoverPosition;
                    await persistState(currentState).ConfigureAwait(false);
                    return cutoverPosition;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to query legacy synapse cursor registry for subscriber {Subscriber}.", subscriberId);
        }

        // 4. Default for brand-new apps: default to 0
        currentState.SignalCursor = 0;
        return 0;
    }
}

/// <summary>
/// Legacy synapse cursor registry contract retained for dual-reader fallback during Release N.
/// </summary>
public interface ISynapseCursorRegistry
{
    /// <summary>Returns the recorded legacy cursor of the subscriber on the publisher; null when not found.</summary>
    Task<long?> GetCursor(string subscriber, NeuronId publisher);
}

/// <summary>
/// Legacy subscriber synapse storage model under "synapses" grain state.
/// </summary>
public sealed class SynapseState
{
    public const string Name = "synapses";
    public List<SynapseSource> Sources { get; set; } = [];
}

/// <summary>
/// Legacy synapse source record.
/// </summary>
public sealed class SynapseSource
{
    public string Source { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public long Cursor { get; set; }
}
