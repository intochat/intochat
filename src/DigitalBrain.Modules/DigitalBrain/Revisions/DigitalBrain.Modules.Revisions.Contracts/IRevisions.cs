using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Revisions;

// The brain's revisions: the queue and the one that is live. Keyed by brain id.
[Alias("revisions.index"), DefaultGrainType("revisions.index")]
public interface IRevisions : INeuron
{
    [ResponseTimeout("00:05:00")] Task<string> Propose(RevisionProposal proposal);
    [ReadOnly, AlwaysInterleave] Task<string[]> List();
    [ReadOnly, AlwaysInterleave] Task<string?> Live();
    Task Ended(string sha);
}