using Orleans.Runtime;

namespace DigitalBrain.Signals;

public interface ILocalSignalHub
{
    void Subscribe(GrainId source, INeuronObserver observer);
    void Unsubscribe(GrainId source, INeuronObserver observer);
}

public interface ILocalSignalFaultSink
{
    void OnError(Exception error);
}
