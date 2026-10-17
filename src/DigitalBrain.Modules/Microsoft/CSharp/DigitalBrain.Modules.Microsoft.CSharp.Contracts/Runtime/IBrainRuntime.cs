using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// One per brain: the behaviors it wants running, their durable subscriptions, and the one sandbox
// session that runs them all.
[Alias("microsoft.csharp.runtime"), DefaultGrainType("microsoft.csharp.runtime")]
public interface IBrainRuntime : INeuron
{
    // A pure read: it never waits for a turn in progress.
    [AlwaysInterleave] Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default);
    // Sign-in and the shell call this: ensures a session when anything is assigned, otherwise nothing.
    Task Touch(CancellationToken cancellationToken = default);
}
