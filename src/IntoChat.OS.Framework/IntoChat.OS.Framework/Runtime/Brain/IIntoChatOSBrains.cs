using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// The host-side seam behind IIntoChatOS: a silo or a client opens the brain a caller is in.
public interface IIntoChatOSBrains
{
    IIntoChatOS Open(string brainId);

    // A neuron by raw grain key, outside any brain: hosts and test harnesses only.
    T Grain<T>(string key) where T : INeuron;

    // Watching needs no brain: the reference already names its grain.
    IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal;
    IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal;
}
