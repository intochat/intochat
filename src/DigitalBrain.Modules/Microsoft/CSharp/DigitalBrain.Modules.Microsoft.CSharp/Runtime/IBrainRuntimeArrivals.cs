using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// One-way so the publishing neuron never waits on this brain's runtime.
[Alias("microsoft.csharp.runtime-arrivals"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeArrivals : IGrainWithStringKey
{
    [OneWay] Task Arrived(Signal signal);
}
