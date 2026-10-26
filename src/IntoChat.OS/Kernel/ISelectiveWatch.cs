namespace IntoChat.OS;

// A watch that tells the runtime which signals to deliver. Callers that only have IIntoChatOS
// keep their own filter; a selective watch never starts a reaction for a signal it will not see.
public interface ISelectiveWatch
{
    IAsyncEnumerable<T> On<T>(INeuron neuron, string selector, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal;

    // The same for a plain neuron's [Signal] records (06-08: an app is plain); source is a proxy from IIntoChatOS.Get.
    IAsyncEnumerable<T> OnPlain<T>(object source, string selector, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal;
}
