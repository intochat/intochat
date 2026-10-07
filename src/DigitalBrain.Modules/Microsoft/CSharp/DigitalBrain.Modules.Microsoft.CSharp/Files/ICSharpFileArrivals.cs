using DigitalBrain;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Files;

// One-way so the publishing neuron never waits while a run starts (possibly the sandbox too).
[Alias("microsoft.csharp.file-arrivals"), DefaultGrainType("microsoft.csharp.file")]
internal interface ICSharpFileArrivals : IGrainWithStringKey
{
    [OneWay] Task Arrived(Signal signal);
}
