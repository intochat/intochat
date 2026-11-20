using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Framework;

// A silo or a cluster client opens a brain. Get addresses an os full key. Open is the session watch.
public interface IIntoChatOSBrains
{
    ISignalWatch Open(string brainId);

    // A neuron by its full key "{brain}/{key}", outside any brain: hosts and test harnesses only.
    T Get<T>(string key) where T : class => (T)Get(typeof(T), key);

    object Get(Type contract, string key);

    // Watching needs no brain: the reference already names its neuron.
    IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal;
}
