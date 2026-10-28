using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Client.Orleans;

// The cluster as a whole: opens brains, resolves neurons for hosts and harnesses, and owns every subscription it handed out
// through their streams. Neurons are DigitalBrain plain neurons, reached through the silo's host grains.
internal sealed class BrainClient(ILogger<BrainClient> logger, IServiceProvider services, IBrainObserver? kernel = null)
    : IIntoChatOSBrains, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;

    public ISignalWatch Open(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return new OrleansBrain(this, brainId);
    }

    // The kernel view the host registered (the silo, or AddIntoChatOSClient for a cluster client); the kernel composition owns it.
    public IBrainObserver Kernel => kernel ?? throw new InvalidOperationException("No kernel view is registered: a silo registers it, and AddIntoChatOSClient does for a cluster client.");

    public object Get(Type contract, string key)
        => SiloNeurons.Get(services, contract, PlainNeuronContracts.IdOf(contract, key));

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
        return PlainSubscription.Open<T>(services, neuron, logger, onSubscribed, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        return ValueTask.CompletedTask;
    }
}
