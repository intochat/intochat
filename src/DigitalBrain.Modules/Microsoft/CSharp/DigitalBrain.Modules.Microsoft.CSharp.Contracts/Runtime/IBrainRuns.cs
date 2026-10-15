using DigitalBrain;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// One per brain, keyed BrainRunsKeys.For(scope): where the brain's synapse runs become facts. It publishes
// BehaviorStarted and BehaviorRan for the brain runtime, which cannot publish them itself because no
// behavior may subscribe to it. Its durable snapshots accompany the existing public signals.
[Alias("microsoft.csharp.runs"), DefaultGrainType("microsoft.csharp.runs")]
public interface IBrainRuns : INeuron
{
    Task<BrainRunsSnapshot> Read(string? excludedOwner = null);
}

// Snapshot consumers need one invalidation per committed revision, not every execution fact.
[GenerateSerializer, Alias("microsoft.csharp.runs-changed")]
public sealed record BrainRunsChanged([property: Id(0)] long Revision) : Signal;
