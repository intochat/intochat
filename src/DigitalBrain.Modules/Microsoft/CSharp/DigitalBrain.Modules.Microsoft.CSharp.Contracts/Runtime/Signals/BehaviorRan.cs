using DigitalBrain;

namespace DigitalBrain.Microsoft.CSharp.Runtime.Signals;

// A synapse finished with what was delivered up to Watermark: Batches batches acknowledged together
// (Succeeded), an exit while they were in flight (Failed, with Error), or Batches signals dropped past the
// buffer cap (Dropped, no duration). Published by the brain's runs neuron under the batch's trace.
[GenerateSerializer, Alias("microsoft.csharp.behavior-ran")]
public sealed record BehaviorRan(
    [property: Id(0)] string RuntimeId, [property: Id(1)] string Owner, [property: Id(2)] string Behavior,
    [property: Id(3)] string Hash, [property: Id(4)] string Source, [property: Id(5)] string SignalType,
    [property: Id(6)] long Watermark, [property: Id(7)] RunOutcome Outcome, [property: Id(8)] int Batches,
    [property: Id(9)] long DurationMicroseconds, [property: Id(10)] long CpuMicroseconds,
    [property: Id(11)] string? Error) : Signal;
