using DigitalBrain;
using DigitalBrain.Client.Runtime;

namespace DigitalBrain.Client;

// One behavior's view of its session: its settings, its stop signal, and neuron calls and signal
// subscriptions made in its name. Disposing it ends nothing shared; the host owns the session.
public sealed class DigitalBrainConnection : IDigitalBrain, IAsyncDisposable
{
    private readonly SessionConnection _session;
    private readonly string _behavior;
    private readonly IReadOnlyDictionary<string, string> _settings;

    internal DigitalBrainConnection(SessionConnection session, string behavior, IReadOnlyDictionary<string, string> settings, CancellationToken stopping)
    {
        _session = session;
        _behavior = behavior;
        _settings = settings;
        Stopping = stopping;
    }

    public CancellationToken Stopping { get; }

    public string? Setting(string name) => _settings.TryGetValue(name, out var value) ? value : null;

    public T Get<T>(NeuronId id) where T : INeuron => NeuronProxy.Create<T>(_session, _behavior, id.Name);

    public IKernel Self => _session.Brain is { Length: > 0 } brain
        ? NeuronProxy.Create<IKernel>(_session, _behavior, brain)
        : throw new InvalidOperationException("This session was not told its brain.");

    public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
    {
        if (source is not NeuronProxy neuron) { throw new ArgumentException("Watch a neuron obtained from this connection's Get.", nameof(source)); }
        return new WatchStream<T>(async ct =>
        {
            var subscription = await SessionSignalSubscription<T>.OpenAsync(_session, _behavior, neuron, ct).ConfigureAwait(false);
            return (subscription.ReadAllAsync(ct), subscription);
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
