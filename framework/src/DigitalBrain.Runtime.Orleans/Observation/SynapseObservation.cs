using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Synapses;
using DigitalBrain.Spi;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>Reads the durable synapse records of one brain out of its journal.</summary>
internal static class SynapseObservation
{
    /// <summary>The neuron id a host grain id names, or null when <paramref name="grainId"/> is not a neuron host.</summary>
    internal static string? SubscriberOf(string grainId)
    {
        foreach (var type in new[] { NeuronTypeRegistry.AffineGrainType, NeuronTypeRegistry.ReentrantGrainType, NeuronTypeRegistry.StatelessGrainType })
        {
            var prefix = type + "/";
            if (grainId.StartsWith(prefix, StringComparison.Ordinal) && grainId.Length > prefix.Length) { return grainId[prefix.Length..]; }
        }
        return null;
    }

    /// <summary>Every synapse stored for <paramref name="brain"/>.</summary>
    internal static async Task<ObservedSynapse[]> List(BrainLogs logs, string brain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        cancellationToken.ThrowIfCancellationRequested();
        var log = await logs.For(brain).WaitAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ObservedSynapse>();
        foreach (var (grain, record) in log.Named(SynapseState.Name))
        {
            if (SubscriberOf(grain) is not { } subscriber) { continue; }
            var state = JournalPayloads.DecodeState<SynapseState>(record);
            if (state is null) { continue; }
            foreach (var source in state.Sources)
            {
                if (source.Source == SynapseEngine.BrainWide || source.Source.Length == 0) { continue; }
                foreach (var alias in source.Aliases)
                {
                    if (alias.Length > 0) { rows.Add(new ObservedSynapse(source.Source, subscriber, alias)); }
                }
            }
        }
        return [.. rows.OrderBy(row => row.From, StringComparer.Ordinal).ThenBy(row => row.To, StringComparer.Ordinal).ThenBy(row => row.Signal, StringComparer.Ordinal)];
    }
}
