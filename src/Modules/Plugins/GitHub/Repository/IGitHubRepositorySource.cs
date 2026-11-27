namespace IntoChat.OS.Modules.Microsoft.GitHub;

internal sealed record ConditionalPollResult<T>(
    bool NotModified,
    IReadOnlyList<T> Items,
    string? ETag);

internal interface IGitHubRepositorySource
{
    Task<PullRequestSnapshot> GetPullRequestAsync(GitHubRepository repository, int number, CancellationToken cancellationToken);
    Task<IReadOnlyList<PullRequestSnapshot>> ListOpenPullRequestsAsync(GitHubRepository repository, CancellationToken cancellationToken);
    Task<ConditionalPollResult<PullRequestSnapshot>> ListOpenPullRequestsConditionalAsync(GitHubRepository repository, string? etag, CancellationToken cancellationToken)
        => Task.FromResult(new ConditionalPollResult<PullRequestSnapshot>(NotModified: true, [], etag));
    Task<GitHubReviewEvidence> GetReviewEvidenceAsync(GitHubRepository repository, PullRequestSnapshot snapshot, CancellationToken cancellationToken);
    Task<RequiredChecksRead> GetRequiredChecksAsync(GitHubRepository repository, string? branch, CancellationToken cancellationToken) => Task.FromResult(new RequiredChecksRead([], false, "Required CI checks have not been discovered for this source."));
    Task PushBranchAsync(GitHubRepository repository, PushBranchRequest request, CancellationToken cancellationToken);
    Task<PullRequestView> CreatePullRequestAsync(GitHubRepository repository, CreatePullRequest request, CancellationToken cancellationToken);
    Task ReplyToReviewCommentAsync(GitHubRepository repository, ReplyReviewComment request, CancellationToken cancellationToken);
    Task MergePullRequestAsync(GitHubRepository repository, MergePullRequest request, CancellationToken cancellationToken);
    Task<IssueSnapshot> GetIssueAsync(GitHubRepository repository, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ConditionalPollResult<IssueSnapshot>> ListOpenIssuesConditionalAsync(GitHubRepository repository, string? etag, CancellationToken cancellationToken)
        => Task.FromResult(new ConditionalPollResult<IssueSnapshot>(NotModified: true, [], etag));
    Task<IssueCreated> CreateIssueAsync(GitHubRepository repository, CreateIssueRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<IssueCommentResult> CommentOnIssueAsync(GitHubRepository repository, int issueNumber, string body, string[] labels, string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ReviewSubmitted> SubmitReviewAsync(GitHubRepository repository, SubmitReviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
