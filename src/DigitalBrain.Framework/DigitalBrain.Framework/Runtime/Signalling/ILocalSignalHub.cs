
namespace DigitalBrain.Framework;

public interface ILocalSignalHub
{
    void Subscribe(GrainId source, INeuronObserver observer);
    void Unsubscribe(GrainId source, INeuronObserver observer);

    // Completes when every signal posted before this call has been handed to its observer.
    Task FlushAsync(CancellationToken cancellationToken);
}

public interface ILocalSignalFaultSink
{
    void OnError(Exception error);
}
