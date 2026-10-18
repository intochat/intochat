
namespace IntoChat.OS.Framework;

public interface ILocalSignalHub
{
    void Subscribe(GrainId source, INeuronObserver observer);
    void Unsubscribe(GrainId source, INeuronObserver observer);
    void Publish(GrainId source, SignalNudge nudge);

    // In-process typed listeners receive every signal published in this silo.
    LocalSignalHub.Subscription<T> Subscribe<T>() where T : Signal;

    // Completes when every nudge posted before this call has been handed to its observer.
    Task FlushAsync(CancellationToken cancellationToken);
}

public interface ILocalSignalFaultSink
{
    void OnError(Exception error);
}
