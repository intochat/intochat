using Orleans.Metadata;

namespace DigitalBrain.Apps;

// The brain's app names, keyed by the brain id. An index, never domain state: every fact
// about an app lives on the app neuron itself.
[Alias("apps.index"), DefaultGrainType("apps.index")]
public interface IAppIndex : INeuron
{
    Task Track(string name);
    Task Untrack(string name);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<string[]> List();
}
