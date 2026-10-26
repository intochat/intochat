using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Testing;

// Every [Signal] record T saved on the test silo from now on, in publish order, with its publisher (06-08): the plain-neuron
// stand-in for the retired local signal hub subscription, read where the operating system listens (IClusterListeners).
public sealed class ClusterTap<T> : IAsyncDisposable where T : DigitalBrain.Signal
{
    private readonly Channel<(DigitalBrain.NeuronId Publisher, T Signal)> _heard = Channel.CreateUnbounded<(DigitalBrain.NeuronId, T)>();
    private IAsyncDisposable? _listening;

    private ClusterTap() { }

    // The signals heard, newest last.
    public ChannelReader<T> Reader => _signals.Reader;

    // The signals heard with their publishers.
    public ChannelReader<(DigitalBrain.NeuronId Publisher, T Signal)> Published => _heard.Reader;

    private readonly Channel<T> _signals = Channel.CreateUnbounded<T>();

    public static async Task<ClusterTap<T>> Open(ITestBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var tap = new ClusterTap<T>();
        tap._listening = await brain.Services.GetRequiredService<DigitalBrain.Spi.IClusterListeners>().Listen<T>("cluster-tap-" + Guid.NewGuid().ToString("N"),
            (publisher, signal, _) =>
            {
                tap._heard.Writer.TryWrite((publisher, signal));
                tap._signals.Writer.TryWrite(signal);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        return tap;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listening is { } listening) { await listening.DisposeAsync().ConfigureAwait(false); }
        _heard.Writer.TryComplete();
        _signals.Writer.TryComplete();
    }
}
