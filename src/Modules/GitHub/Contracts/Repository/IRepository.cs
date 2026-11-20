using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// The connection neuron's grain type is its connector id, "github".
[DigitalBrain.Contracts.NeuronAlias("github.repository"), DigitalBrain.Contracts.DefaultNeuronType("github")]
[Shareable]
public interface IRepository
{
    const string KeyPattern = "connection/github/{repo}";

    Task<RepositoryView> Refresh(RefreshRepository request);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<RepositoryView> Read();

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<PullRequestsRead> ReadPullRequests();

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default);

    Task PushBranch(PushBranchRequest request);

    Task<PullRequestView> CreatePullRequest(CreatePullRequest request);

    Task ReplyToReviewComment(ReplyReviewComment request);

    Task MergePullRequest(MergePullRequest request);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IssueRead> ReadIssue(ReadIssue query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IssuesRead> ReadIssues() => throw new NotSupportedException();

    Task<IssueCommentPreview> PreviewIssueComment(PreviewIssueCommentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IssueCommentResult> CommentOnIssue(CommentOnIssueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IssueCommentPreview[]> ReadPendingIssueDrafts() => throw new NotSupportedException();

    Task<bool> DismissIssueDraft(string previewId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
