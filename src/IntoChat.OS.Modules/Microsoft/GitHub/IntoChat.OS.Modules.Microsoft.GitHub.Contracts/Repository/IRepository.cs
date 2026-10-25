using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// The connection neuron's grain type is its connector id, "github".
[Alias("github.repository")]
[Orleans.Metadata.DefaultGrainType("github")]
public interface IRepository : INeuron
{
    Task<RepositoryView> Refresh(RefreshRepository request);

    [ReadOnly]
    Task<RepositoryView> Read();

    [ReadOnly]
    Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default);

    [ReadOnly]
    Task<PullRequestsRead> ReadPullRequests();

    [ReadOnly]
    Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default);

    [ReadOnly]
    Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default);
}
