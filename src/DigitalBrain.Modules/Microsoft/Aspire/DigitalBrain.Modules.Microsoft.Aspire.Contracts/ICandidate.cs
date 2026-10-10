using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.candidate.stage")]
public enum CandidateStage { Proposed, Queued, Building, Starting, Healthy, Verifying, Verified, Promoting, Promoted, Discarded, Failed }

[GenerateSerializer, Alias("aspire.candidate.endpoints")]
public sealed record CandidateEndpoints([property: Id(0)] string Mcp, [property: Id(1)] string Ui);

[GenerateSerializer, Alias("aspire.candidate.snapshot")]
public sealed record CandidateSnapshot(
    [property: Id(0)] string Sha,
    [property: Id(1)] string Branch,
    [property: Id(2)] string Summary,
    [property: Id(3)] CandidateStage Stage,
    [property: Id(4)] CandidateEndpoints? Endpoints,
    [property: Id(5)] VerificationPlan? Plan,
    [property: Id(6)] VerificationReport? Report,
    [property: Id(7)] string? Failure,
    [property: Id(8)] CandidateStage[] History);

[GenerateSerializer, Alias("aspire.candidate.proposal")]
public sealed record CandidateProposal(
    [property: Id(0)] string Branch,
    [property: Id(1)] string Summary,
    [property: Id(2)] Dictionary<string, string> Files,
    [property: Id(3)] VerificationPlan Plan);

[Alias("aspire.candidate"), DefaultGrainType("aspire.candidate")]
public interface ICandidate : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<CandidateSnapshot> Read();
    Task<CandidateSnapshot> Propose(string branch, string summary, VerificationPlan plan);
    Task<CandidateSnapshot> Queue();
    [AlwaysInterleave, ResponseTimeout("00:20:00")] Task<CandidateSnapshot> Start();
    [AlwaysInterleave, ResponseTimeout("00:30:00")] Task<CandidateSnapshot> Verify();
    [ResponseTimeout("00:10:00")] Task<CandidateSnapshot> Promote();
    Task<CandidateSnapshot> Discard();
    // Starts without holding the caller: the index must be free when a failing Start reports Ended.
    [OneWay, AlwaysInterleave] Task Begin();
    // Completes a Promoting candidate once the cluster runs its build; a no-op in any other stage.
    Task<CandidateSnapshot> Finish();
    static string Key(string brainId, string sha) => $"{brainId}/aspire/candidate/{sha}";
}

// The per-brain index: writes the files into a fresh worktree, queues, starts the next.
[Alias("aspire.candidates"), DefaultGrainType("aspire.candidates")]
public interface ICandidates : INeuron
{
    [ResponseTimeout("00:05:00")] Task<string> Propose(CandidateProposal proposal);
    [ReadOnly, AlwaysInterleave] Task<string?> Live();
    [ReadOnly, AlwaysInterleave] Task<string[]> Known();
    // One-way: a candidate ends from inside calls the index may itself be awaiting (cluster reactivation inside Propose).
    [OneWay] Task Ended(string sha);
    static string Key(string brainId) => $"{brainId}/aspire/candidates";
}
