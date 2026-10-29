using IntoChat.OS;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Client;

// One behavior's view of its session: its settings, its stop signal, and neuron calls and signal
// subscriptions made in its name. Disposing it ends nothing shared; the host owns the session.
public sealed class IntoChatOSConnection : ISignalWatch, ISelectiveWatch, IAsyncDisposable, IInitialSignalWait
{
    private readonly SessionConnection _session;
    private readonly string _behavior;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly BehaviorTrace _trace;
    private readonly string? _assignmentHash;
    private readonly string? _assignmentRevision;
    private readonly AsyncLocal<int> _nestedWait = new();
    private readonly Lock _initialGate = new();
    private Task? _initialized;

    internal IntoChatOSConnection(SessionConnection session, string behavior, IReadOnlyDictionary<string, string> settings, CancellationToken stopping, BehaviorTrace trace, string? assignmentHash = null, string? assignmentRevision = null)
    {
        _session = session;
        _behavior = behavior;
        _settings = settings;
        Stopping = stopping;
        _trace = trace;
        _assignmentHash = assignmentHash;
        _assignmentRevision = assignmentRevision;
    }

    public CancellationToken Stopping { get; }

    public string? Setting(string name) => _settings.TryGetValue(name, out var value) ? value : null;

    // Scripts hold this class: Get and Neuron resolve any neuron contract over the session edge.
    // osFullKey is the os key "{brain}/{key}", not a DigitalBrain neuron id.
    public T Get<T>(string osFullKey) where T : class => (T)NeuronProxy.Create(typeof(T), _session, _behavior, osFullKey, _trace);

    // The edge resolves the wire key raw, so a relative name leaves the sandbox as "{brain}/{relative}".
    public T Neuron<T>(string relative) where T : class => _session.Brain is { Length: > 0 } brain
        ? Get<T>($"{brain}/{relative}")
        : throw new InvalidOperationException("This session was not told its brain.");

    public IBrain Self => _session.Brain is { Length: > 0 } brain
        ? (IBrain)NeuronProxy.Create(typeof(IBrain), _session, _behavior, IBrain.Key(brain), _trace)
        : throw new InvalidOperationException("This session was not told its brain.");

    public SessionKernel Kernel => new SessionKernel(_session, _behavior, _trace);

    // A neuron's [Signal] records reach the behavior through the runtime's session synapse.
    public IAsyncEnumerable<T> On<T>(object neuron, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Watch<T>(neuron, static () => Task.CompletedTask, selector: null, cancellationToken);

    // Reconcile after registration: a state change either precedes the snapshot or is queued.
    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Watch<T>(neuron, onSubscribed, selector: null, cancellationToken);

    public IAsyncEnumerable<T> On<T>(object neuron, string selector, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(neuron);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return Watch<T>(neuron, onSubscribed, selector, cancellationToken);
    }

    private IAsyncEnumerable<T> Watch<T>(object source, Func<Task> onSubscribed, string? selector, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        if (source is not NeuronProxy neuron) { throw new ArgumentException("Watch a neuron obtained from this connection's Get.", nameof(source)); }
        return new WatchStream<T>(async ct =>
        {
            var subscription = await SessionSignalSubscription<T>.OpenAsync(_session, _behavior, neuron, _trace, ct, selector).ConfigureAwait(false);
            return (subscription.ReadAllAsync(ct), subscription);
        }, cancellationToken, Stopping, async () =>
        {
            await onSubscribed().ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
            await ((IInitialSignalWait)this).ReachedInitialWaitAsync(wait.Token).ConfigureAwait(false);
        });
    }

    // Scripts hold this class, not the interface, so the vocabulary's set and rebuild entry points are reachable here.
    public IAsyncEnumerable<T> On<T>(Sources sources, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Sources.Stream<T>(this, sources, cancellationToken);

    public Task React<T>(object neuron, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Sources.React<T>(this, Sources.Of(neuron), rebuild, cancellationToken);

    public Task React<T>(Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Sources.React<T>(this, sources, rebuild, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    IDisposable IInitialSignalWait.SuppressInitialWait()
    {
        var previous = _nestedWait.Value;
        _nestedWait.Value = previous + 1;
        return new RestoreWait(() => _nestedWait.Value = previous);
    }

    Task IInitialSignalWait.ReachedInitialWaitAsync(CancellationToken cancellationToken)
    {
        if (_nestedWait.Value != 0 || _assignmentHash is null) { return Task.CompletedTask; }
        lock (_initialGate)
        {
            return _initialized ??= ReportInitialWaitAsync(cancellationToken);
        }
    }

    private async Task ReportInitialWaitAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            try
            {
                await _session.ReportStatusAsync(new(_behavior, _assignmentHash!, "Initialized", null, null, _assignmentRevision), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Pending signals remain durable while the initialized script retries its marker.
                // Bound the backoff, not delivery attempts: an idle script may never get another signal.
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    private sealed class RestoreWait(Action restore) : IDisposable { public void Dispose() => restore(); }
}
