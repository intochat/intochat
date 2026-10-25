using DigitalBrain.Spi;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>
/// <see cref="ISignalHistory"/> of one brain over its BrainLog (04 R4-06): a per-publisher read uses publisher sequences, a
/// brain-wide read uses brain positions. Payloads decode through the closed <see cref="SignalTypeMap"/>.
/// </summary>
internal sealed class BrainSignalHistory(BrainLogs logs, BrainId brain) : ISignalHistory
{
    public BrainId Brain { get; } = brain;

    /// <exception cref="UnauthorizedAccessException"><paramref name="publisher"/> belongs to another brain (T-04-13).</exception>
    public async Task<SignalPage> Read(NeuronId publisher, long since, int limit, CancellationToken ct)
    {
        SignalPage.CheckLimit(limit);
        if (publisher.Brain != Brain) { throw new UnauthorizedAccessException($"Neuron {publisher} is not in brain {Brain}."); }
        var log = await logs.For(Brain.Value);
        var slice = await log.PublisherSignals(publisher.ToString(), Math.Max(0, since), limit, ct);
        return Page(log.Payloads, slice, publisher, positions: false);
    }

    public async Task<SignalPage> Read(long since, int limit, CancellationToken ct)
    {
        SignalPage.CheckLimit(limit);
        var log = await logs.For(Brain.Value);
        var slice = await log.BrainSignals(Math.Max(0, since), limit, ct);
        return Page(log.Payloads, slice, null, positions: true);
    }

    private static SignalPage Page(JournalPayloads payloads, StoredSlice slice, NeuronId? publisher, bool positions)
        => new(slice.Floor, slice.Last, [.. slice.Held
            .Where(item => publisher is not null || NeuronId.TryParse(item.Publisher, out _)) // only neurons publish history
            .Select(item => new JournaledSignal(
            publisher ?? NeuronId.Parse(item.Publisher),
            positions ? item.Position : item.Sequence,
            payloads.DecodeSignal(item.Payload),
            DateTimeOffset.FromUnixTimeMilliseconds(item.AtUnixMs)))]);
}

/// <summary>Hands out the <see cref="ISignalHistory"/> of a brain; the host context (04-11) asks it per neuron.</summary>
internal sealed class BrainSignalHistories(BrainLogs logs)
{
    public ISignalHistory For(BrainId brain) => new BrainSignalHistory(logs, brain);
}
