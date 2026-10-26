using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Client.Orleans;

// The cluster as a whole: opens brains, resolves raw grains for hosts and harnesses, and owns
// every subscription so disposal releases them all.
internal sealed class BrainClient(IClusterClient cluster, IOptions<SubscriptionOptions> options, ILogger<BrainClient> logger, ILocalSignalHub? hub = null,
    IKernelView? kernel = null, IntoChat.OS.Runtime.IPlainNeurons? plainNeurons = null, IntoChat.OS.Runtime.IPlainSignals? plainSignals = null)
    : IIntoChatOSBrains, IBrainSubscriptions, IntoChat.OS.Runtime.IPlainClient, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<IAsyncDisposable> _subscriptions = [];
    private bool _disposed;

    internal int TrackedSubscriptions { get { lock (_gate) { return _subscriptions.Count; } } }

    public IIntoChatOS Open(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return new OrleansBrain(this, brainId);
    }

    // A connection key names its grain type (several connectors share IConnection); any other key
    // resolves by the contract's default grain type.
    public IGrainFactory Grains => cluster;

    // The kernel view the host registered (the silo, or AddIntoChatOSClient for a cluster client); the kernel composition owns it.
    public IKernelView Kernel => kernel ?? throw new InvalidOperationException("No kernel view is registered: a silo registers it, and AddIntoChatOSClient does for a cluster client.");

    // Connections are plain neurons (06-08), reached through the plain seam; a still-legacy neuron is its grain.
    public T Grain<T>(string key) where T : INeuron => cluster.GetGrain<T>(key);

    // Transitional (06-07, R4-15): plain neurons are reached through the kernel composition's seam, which a silo registers.
    public object Plain(Type contract, string key)
        => (plainNeurons ?? throw new NotSupportedException("This host does not reach plain neurons: the kernel composition registers them.")).Get(contract, key);

    object IntoChat.OS.Runtime.IPlainClient.GetPlain(Type contract, NeuronId id) => Plain(contract, id.Name);

    public IAsyncEnumerable<T> OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
        return PlainSubscription.Open<T>(plainSignals ?? throw new NotSupportedException("This host does not reach plain neurons' signals: the kernel composition registers them."),
            source, logger, onSubscribed, cancellationToken);
    }

    // Either token ends the watch: the one given here, or the enumerator's. See WatchStream for
    // why the stream is hand-written.
    public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => On<T>(source, static () => Task.CompletedTask, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        return new WatchStream<T>(async ct =>
        {
            if (!cancellationToken.CanBeCanceled)
            {
                var subscription = await SubscribeAsync<T>(source, ct).ConfigureAwait(false);
                return (subscription.ReadAllAsync(ct), subscription);
            }
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            try
            {
                var subscription = await SubscribeAsync<T>(source, linked.Token).ConfigureAwait(false);
                return (subscription.ReadAllAsync(linked.Token), new LinkedHandle(subscription, linked));
            }
            catch { linked.Dispose(); throw; }
        }, default, default, onSubscribed);
    }

    private sealed class LinkedHandle(IAsyncDisposable subscription, CancellationTokenSource linked) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await subscription.DisposeAsync().ConfigureAwait(false); }
            finally { linked.Dispose(); }
        }
    }

    public async Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        SignalSubscription<T> subscription;
        Task connecting;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscription = new(cluster, source, options.Value, logger, Remove, hub, cancellationToken);
            _subscriptions.Add(subscription); // Orleans holds observer targets weakly; this owns the strong reference.
            connecting = subscription.ConnectAsync();
        }
        await connecting.ConfigureAwait(false);
        return subscription;
    }

    private void Remove(IAsyncDisposable subscription)
    {
        lock (_gate) { _subscriptions.Remove(subscription); }
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] subscriptions;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            subscriptions = [.. _subscriptions];
        }
        await Task.WhenAll(subscriptions.Select(subscription => subscription.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
