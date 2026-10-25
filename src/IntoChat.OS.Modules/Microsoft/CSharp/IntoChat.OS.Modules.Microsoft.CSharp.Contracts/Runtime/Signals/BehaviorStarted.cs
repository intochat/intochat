using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;

// A batch reached a running synapse: published once per subscription and watermark, by the brain's runs
// neuron. Hash is the synapse's own assembly hash; the stamped Trace is the batch's.
[GenerateSerializer, Alias("microsoft.csharp.behavior-started")]
public sealed record BehaviorStarted(
    [property: Id(0)] string RuntimeId, [property: Id(1)] string Owner, [property: Id(2)] string Behavior,
    [property: Id(3)] string Hash, [property: Id(4)] string Source, [property: Id(5)] string SignalType,
    [property: Id(6)] long Watermark) : Signal;
