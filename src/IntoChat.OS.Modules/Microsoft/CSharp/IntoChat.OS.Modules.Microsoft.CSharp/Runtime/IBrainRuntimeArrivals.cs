using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// One-way so the publishing neuron never waits on this brain's runtime.
[Alias("microsoft.csharp.runtime-arrivals"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeArrivals : IGrainWithStringKey
{
    // Reads every source's part log from its subscriptions' watermarks; sent by a reactivated runtime to itself.
    [OneWay] Task CatchUp();

    // Reads one source's part log from its subscriptions' watermarks, once the catch-up watched it.
    [OneWay] Task Read(string source);
}
