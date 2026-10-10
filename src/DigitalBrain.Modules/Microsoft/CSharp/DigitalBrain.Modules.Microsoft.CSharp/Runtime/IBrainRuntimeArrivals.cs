using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// One-way so the publishing neuron never waits on this brain's runtime.
[Alias("microsoft.csharp.runtime-arrivals"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeArrivals : IGrainWithStringKey
{
    [OneWay] Task Arrived(Signal signal);

    // Reads every source's part log from its subscriptions' watermarks; sent by a reactivated runtime to itself.
    [OneWay] Task CatchUp();
}
