namespace IntoChat.OS.Framework;

// The host-side seam behind IIntoChatOS: a silo or a client opens the brain a caller is in.
public interface IIntoChatOSBrains
{
    IIntoChatOS Open(string brainId);

    // A neuron by its full key "{brain}/{key}", outside any brain: hosts and test harnesses only.
    T Get<T>(string key) where T : class => (T)Get(typeof(T), key);

    object Get(Type contract, string key);

    // Watching needs no brain: the reference already names its neuron.
    IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal;
}
