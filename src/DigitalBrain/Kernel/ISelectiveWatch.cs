namespace DigitalBrain;

// A watch that tells the runtime which signals to deliver. Callers that only have IDigitalBrain
// keep their own filter; a selective watch never starts a reaction for a signal it will not see.
public interface ISelectiveWatch
{
    IAsyncEnumerable<T> On<T>(INeuron neuron, string selector, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal;
}
