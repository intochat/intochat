namespace IntoChat.OS;

// A watch that tells the runtime which signals to deliver. Callers that only have IIntoChatOS
// keep their own filter; a selective watch never starts a reaction for a signal it will not see.
public interface ISelectiveWatch
{
    // A neuron's [Signal] records that match selector; neuron is a reference from IIntoChatOS.Get.
    IAsyncEnumerable<T> On<T>(object neuron, string selector, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal;
}
