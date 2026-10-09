using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Revisions;

// One proposed change to the brain's own source, running as a sibling cluster until promoted or discarded.
[Alias("revisions.revision"), DefaultGrainType("revisions.revision")]
public interface IRevision : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<RevisionSnapshot> Read();
    Task<RevisionSnapshot> Propose(string branch, string worktreePath, string summary, VerificationPlan plan);
    Task<RevisionSnapshot> Queue();
    [AlwaysInterleave, ResponseTimeout("00:15:00")] Task<RevisionSnapshot> Start();
    [AlwaysInterleave, ResponseTimeout("00:30:00")] Task<RevisionSnapshot> Verify(VerificationPlan plan);
    [ResponseTimeout("00:05:00")] Task<RevisionSnapshot> Promote();
    Task<RevisionSnapshot> Discard();

    static string Key(string brainId, string sha) => $"{brainId}/revisions/{sha}";
}