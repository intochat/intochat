using IntoChat.OS.Framework;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Client.Orleans;

// The cluster as a whole: opens brains, resolves neurons for hosts and harnesses, and owns every subscription it handed out
// through their streams. Neurons are DigitalBrain plain neurons, reached through the kernel composition's seam (06-08).
internal sealed class BrainClient(ILogger<BrainClient> logger, IKernelView? kernel = null,
    IntoChat.OS.Runtime.IPlainNeurons? plainNeurons = null, IntoChat.OS.Runtime.IPlainSignals? plainSignals = null)
    : IIntoChatOSBrains, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;

    public IIntoChatOS Open(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return new OrleansBrain(this, brainId);
    }

    // The kernel view the host registered (the silo, or AddIntoChatOSClient for a cluster client); the kernel composition owns it.
    public IKernelView Kernel => kernel ?? throw new InvalidOperationException("No kernel view is registered: a silo registers it, and AddIntoChatOSClient does for a cluster client.");

    public object Get(Type contract, string key)
        => (plainNeurons ?? throw new NotSupportedException("This host does not reach neurons: the kernel composition registers them.")).Get(contract, key);

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(onSubscribed);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
        return PlainSubscription.Open<T>(plainSignals ?? throw new NotSupportedException("This host does not reach neurons' signals: the kernel composition registers them."),
            neuron, logger, onSubscribed, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        return ValueTask.CompletedTask;
    }
}
