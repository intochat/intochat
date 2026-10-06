namespace DigitalBrain;

// The brain as a program sees it: resolve a neuron, watch a neuron. No handles; the loop is the connection.
public interface IDigitalBrain
{
    T Get<T>(string id) where T : class, INeuron;
    IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal;
}
