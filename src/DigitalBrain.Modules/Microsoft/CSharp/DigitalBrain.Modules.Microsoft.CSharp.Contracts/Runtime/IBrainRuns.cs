using DigitalBrain;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// One per brain, keyed BrainRunsKeys.For(scope): where the brain's synapse runs become facts. It publishes
// BehaviorStarted and BehaviorRan for the brain runtime, which cannot publish them itself because no
// behavior may subscribe to it. A signal source only: brain.On<BehaviorRan>(brain.Get<IBrainRuns>(...)).
[Alias("microsoft.csharp.runs"), DefaultGrainType("microsoft.csharp.runs")]
public interface IBrainRuns : INeuron;
