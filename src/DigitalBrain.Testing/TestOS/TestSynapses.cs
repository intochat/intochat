using DigitalBrain.Spi;

namespace DigitalBrain.Testing;

// The synapses one activation holds as a subscriber; durable entries live in the TestOS registry.
internal sealed class TestSynapses(TestNeuronHost host) : ISynapses
{
    public Task Ensure<TSignal>(NeuronId from) where TSignal : Signal => Ensure(from, typeof(TSignal));

    public Task Ensure(NeuronId from, params Type[] signals) => host.OS.Synapses.Ensure(host, from, signals);

    public Task Remove(NeuronId from)
    {
        host.OS.Synapses.Remove(host.Id, from);
        return Task.CompletedTask;
    }
}

// Durable synapses of a TestOS: per subscriber, per source, the signal types and the cursor. They survive deactivation.
internal sealed class SynapseRegistry(TestOS os)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<NeuronId, Dictionary<NeuronId, Entry>> _bySubscriber = [];
    private long _duplicates;

    public long DuplicatesDropped => Interlocked.Read(ref _duplicates);

    public async Task Ensure(TestNeuronHost subscriber, NeuronId from, Type[] signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (subscriber.Class.IsDefined(typeof(StatelessNeuronAttribute), false))
        {
            throw new InvalidOperationException($"{subscriber.Class.FullName} is a [StatelessNeuron] and holds no synapses.");
        }
        if (from == subscriber.Id) { throw new ArgumentException("A neuron does not subscribe to itself.", nameof(from)); }
        if (signals.Length == 0) { throw new ArgumentException("A synapse names at least one signal type.", nameof(signals)); }
        foreach (var signal in signals)
        {
            if (signal is null || !typeof(Signal).IsAssignableFrom(signal)) { throw new ArgumentException($"{signal} is not a signal type.", nameof(signals)); }
        }
        if (from.Brain != subscriber.Id.Brain) { throw new UnauthorizedAccessException($"Neuron {subscriber.Id} may not subscribe to {from} of another brain."); }

        // Admission is a turn on the source, through the runtime's single entry point.
        IReadOnlyList<Type> named = [.. signals];
        var source = os.Host(from);
        await os.Track(source.Turn(interleave: false, async neuron =>
        {
            await NeuronAttachment.Admit(neuron, subscriber.Id, named).ConfigureAwait(false);
            return 0;
        })).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_bySubscriber.TryGetValue(subscriber.Id, out var sources)) { _bySubscriber[subscriber.Id] = sources = []; }
            if (!sources.TryGetValue(from, out var entry))
            {
                sources[from] = entry = new Entry(from, subscriber.Id) { Cursor = os.Journal(from.Brain).Last(from), CursorAt = os.Clock.GetUtcNow() };
            }
            entry.Signals.UnionWith(named);
        }
    }

    public void Remove(NeuronId subscriber, NeuronId from)
    {
        lock (_gate)
        {
            if (_bySubscriber.TryGetValue(subscriber, out var sources)) { sources.Remove(from); }
        }
    }

    public IReadOnlyList<Synapse> Of(NeuronId subscriber)
    {
        lock (_gate)
        {
            return _bySubscriber.TryGetValue(subscriber, out var sources)
                ? [.. sources.Values.SelectMany(entry => entry.Signals.Select(signal => new Synapse(entry.Source, signal, subscriber))).OrderBy(synapse => synapse.From.ToString(), StringComparer.Ordinal).ThenBy(synapse => synapse.Signal.FullName, StringComparer.Ordinal)]
                : [];
        }
    }

    public long? Cursor(NeuronId subscriber, NeuronId source)
    {
        lock (_gate) { return _bySubscriber.TryGetValue(subscriber, out var sources) && sources.TryGetValue(source, out var entry) ? entry.Cursor : null; }
    }

    // The lowest cursor on the publisher that advanced within StaleCursorAfter; stale cursors stop pinning the floor.
    public long? LowestLiveCursor(NeuronId publisher)
    {
        var cutoff = os.Clock.GetUtcNow() - os.Options.StaleCursorAfter;
        lock (_gate)
        {
            var live = _bySubscriber.Values.Select(sources => sources.GetValueOrDefault(publisher)).Where(entry => entry is not null && entry.CursorAt > cutoff).Select(entry => entry!.Cursor).ToList();
            return live.Count == 0 ? null : live.Min();
        }
    }

    public void Activated(TestNeuronHost subscriber) => Interlocked.Add(ref _duplicates, subscriber is null ? 1 : 0);

    public void Appended(NeuronId publisher, IReadOnlyList<JournaledSignal> written) => Interlocked.Add(ref _duplicates, written.Count == 0 && publisher == default ? 1 : 0);

    private sealed class Entry(NeuronId source, NeuronId subscriber)
    {
        public NeuronId Source { get; } = source;

        public NeuronId Subscriber { get; } = subscriber;

        public HashSet<Type> Signals { get; } = [];

        public long Cursor { get; set; }

        public DateTimeOffset CursorAt { get; set; }
    }
}
