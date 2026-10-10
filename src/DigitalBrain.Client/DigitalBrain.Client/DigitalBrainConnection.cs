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
    private readonly BehaviorTrace _trace;

    internal DigitalBrainConnection(SessionConnection session, string behavior, IReadOnlyDictionary<string, string> settings, CancellationToken stopping, BehaviorTrace trace)
    {
        _session = session;
        _behavior = behavior;
        _settings = settings;
        Stopping = stopping;
        _trace = trace;
    }

    public CancellationToken Stopping { get; }

    public string? Setting(string name) => _settings.TryGetValue(name, out var value) ? value : null;

    public T Get<T>(NeuronId id) where T : INeuron => NeuronProxy.Create<T>(_session, _behavior, id.Name, _trace);

    public IBrain Self => _session.Brain is { Length: > 0 } brain
        ? NeuronProxy.Create<IBrain>(_session, _behavior, brain, _trace)
        : throw new InvalidOperationException("This session was not told its brain.");

    public IKernelView Kernel => new SessionKernel(_session, _behavior, _trace);

    public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => On<T>(source, static () => Task.CompletedTask, cancellationToken);

    // Reconcile after registration: a state change either precedes the snapshot or is queued.
    public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        if (source is not NeuronProxy neuron) { throw new ArgumentException("Watch a neuron obtained from this connection's Get.", nameof(source)); }
        return new WatchStream<T>(async ct =>
        {
            var subscription = await SessionSignalSubscription<T>.OpenAsync(_session, _behavior, neuron, _trace, ct).ConfigureAwait(false);
            return (subscription.ReadAllAsync(ct), subscription);
        }, cancellationToken, Stopping, onSubscribed);
    }

    // Scripts hold this class, not the interface, so the vocabulary's set and rebuild entry points are reachable here.
    public IAsyncEnumerable<T> On<T>(Sources sources, CancellationToken cancellationToken = default) where T : Signal
        => Sources.Stream<T>(this, sources, cancellationToken);

    public Task React<T>(INeuron neuron, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : Signal
        => Sources.React<T>(this, Sources.Of(neuron), rebuild, cancellationToken);

    public Task React<T>(Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : Signal
        => Sources.React<T>(this, sources, rebuild, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
