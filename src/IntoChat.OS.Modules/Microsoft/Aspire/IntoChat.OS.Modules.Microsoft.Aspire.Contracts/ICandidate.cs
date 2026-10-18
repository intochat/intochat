using IntoChat.OS;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.Aspire;

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
    // Start and Verify mutate the candidate: they run one at a time, never beside Discard or another Start.
    [ResponseTimeout("00:20:00")] Task<CandidateSnapshot> Start();
    [ResponseTimeout("00:30:00")] Task<CandidateSnapshot> Verify();
    [ResponseTimeout("00:10:00")] Task<CandidateSnapshot> Promote();
    Task<CandidateSnapshot> Discard();
    // Starts without holding the caller: the index must be free when a failing Start reports Ended.
    // A no-op unless the candidate is Proposed or Queued, so the index may re-send it to heal a lost one.
    [OneWay] Task Begin();
    // The cluster's activation settles a Promoting candidate: Finish once the cluster runs its build, Abandon
    // when the restart never took. Both interleave (Promote may be what activated the cluster) and are no-ops
    // in any other stage.
    [AlwaysInterleave] Task<CandidateSnapshot> Finish();
    [AlwaysInterleave] Task<CandidateSnapshot> Abandon(string reason);
    static string Key(string brainId, string sha) => $"{brainId}/{Relative(sha)}";
    // The id inside its brain.
    static string Relative(string sha) => $"aspire/candidate/{sha}";
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
    // One-way calls are at most once: re-derives Live and Queue from the candidates' own stages, applying a lost
    // Ended and re-sending a lost Begin. Runs on every Propose and on the cluster's activation.
    [OneWay] Task Heal();
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "aspire/candidates";
}
