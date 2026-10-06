using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Signals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Client.Orleans;

internal sealed class BrainClient(IClusterClient cluster, IOptions<SubscriptionOptions> options, ILogger<BrainClient> logger, ILocalSignalHub? hub = null)
    : IDigitalBrain, IBrainSubscriptions, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<IAsyncDisposable> _subscriptions = [];
    private bool _disposed;

    internal int TrackedSubscriptions { get { lock (_gate) { return _subscriptions.Count; } } }

    public T Get<T>(string id) where T : class, INeuron => cluster.GetGrain<T>(id);

    public async IAsyncEnumerable<T> On<T>(INeuron source, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : Signal
    {
        await using var subscription = await SubscribeAsync<T>(source, cancellationToken).ConfigureAwait(false);
        await foreach (var signal in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false)) { yield return signal; }
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
