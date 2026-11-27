using DigitalBrain.Contracts;

namespace IntoChat.Aspire;

public enum CandidateStage { Proposed, Queued, Building, Starting, Healthy, Verifying, Verified, Promoting, Promoted, Discarded, Failed }

public enum CandidateHostMode { Process = 1, AgentDevSlots = 2 }

public sealed record CandidateEndpoints(string Mcp, string Ui);

public sealed record CandidateSnapshot(
    string Sha,
    string Branch,
    string Summary,
    CandidateStage Stage,
    CandidateEndpoints? Endpoints,
    VerificationPlan? Plan,
    VerificationReport? Report,
    string? Failure,
    CandidateStage[] History);

public sealed record CandidateProposal(
    string Branch,
    string Summary,
    Dictionary<string, string> Files,
    VerificationPlan Plan);

// A plain contract since 06-06d.
[NeuronAlias("aspire.candidate", Formerly = ["IntoChat.OS.Modules.Microsoft.Aspire.ICandidate"]), DefaultNeuronType("aspire.candidate")]
public interface ICandidate
{
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<CandidateSnapshot> Read();
    Task<CandidateSnapshot> Propose(string branch, string summary, VerificationPlan plan);
    Task<CandidateSnapshot> Queue();
    // Start and Verify mutate the candidate: they run one at a time, never beside Discard or another Start.
    [DigitalBrain.Contracts.CallTimeout(1_200_000)] Task<CandidateSnapshot> Start();
    [DigitalBrain.Contracts.CallTimeout(1_800_000)] Task<CandidateSnapshot> Verify();
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<CandidateSnapshot> Promote();
    Task<CandidateSnapshot> Discard();
    // Starts without holding the caller: the index must be free when a failing Start reports Ended.
    // A no-op unless the candidate is Proposed or Queued, so the index may re-send it to heal a lost one.
    [DigitalBrain.Contracts.OneWayCall] Task Begin();
    // The cluster's activation settles a Promoting candidate: Finish once the cluster runs its build, Abandon
    // when the restart never took. Both interleave (Promote may be what activated the cluster) and are no-ops
    // in any other stage.
    [DigitalBrain.Contracts.OneWayCall] Task Finish();
    [DigitalBrain.Contracts.OneWayCall] Task Abandon(string reason);
    static string Key(string brainId, string sha) => $"{brainId}/{Relative(sha)}";
    // The id inside its brain.
    static string Relative(string sha) => $"aspire/candidate/{sha}";
}

// The per-brain index: writes the files into a fresh worktree, queues, starts the next.
// A plain contract since 06-06d.
[NeuronAlias("aspire.candidates", Formerly = ["IntoChat.OS.Modules.Microsoft.Aspire.ICandidates"]), DefaultNeuronType("aspire.candidates")]
public interface ICandidates
{
    [DigitalBrain.Contracts.CallTimeout(300_000)] Task<string> Propose(CandidateProposal proposal);
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<string?> Live();
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<string[]> Known();
    // One-way: a candidate ends from inside calls the index may itself be awaiting (cluster reactivation inside Propose).
    [DigitalBrain.Contracts.OneWayCall] Task Ended(string sha);
    // One-way calls are at most once: re-derives Live and Queue from the candidates' own stages, applying a lost
    // Ended and re-sending a lost Begin. Runs on every Propose and on the cluster's activation.
    [DigitalBrain.Contracts.OneWayCall] Task Heal();
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "aspire/candidates";
}
