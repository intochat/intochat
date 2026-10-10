using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Revisions;

// Which of the brain's revisions is live and which wait behind it. Keyed by brain id. The revisions
// themselves are kernel instances of IRevision; worktrees, ports and processes are the launcher's.
[Alias("revisions.index"), DefaultGrainType("revisions.index")]
public interface IRevisions : INeuron
{
    [ResponseTimeout("00:05:00")] Task<string> Propose(RevisionProposal proposal);
    [ReadOnly, AlwaysInterleave] Task<string?> Live();
    Task Ended(string sha);
}