using Orleans.Concurrency;

namespace DigitalBrain;

public interface INeuron : IGrainWithStringKey
{
    // A subscriber starting inside its source's turn (an app deploying its own behavior) watches
    // that busy source; adding an observer is synchronous, so it interleaves safely.
    [AlwaysInterleave] Task<Guid> Watch(INeuronObserver observer);
    // A turn that stops a subscriber, such as an app uninstalling its script, must not deadlock on
    // that subscriber's final Unwatch. Removing an observer is safe while signals are being delivered.
    [AlwaysInterleave] Task Unwatch(INeuronObserver observer);
}
