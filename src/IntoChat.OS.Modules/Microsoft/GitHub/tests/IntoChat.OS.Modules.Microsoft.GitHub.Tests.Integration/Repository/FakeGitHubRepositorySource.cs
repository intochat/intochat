namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Integration.Repository;

internal sealed class FakeGitHubRepositorySource : IGitHubRepositorySource
{
    public Task<PullRequestSnapshot> GetPullRequestAsync(GitHubRepository repository, int number, CancellationToken cancellationToken)
        => Task.FromResult(Snapshot(repository, number));

    public Task<IReadOnlyList<PullRequestSnapshot>> ListOpenPullRequestsAsync(GitHubRepository repository, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PullRequestSnapshot>>([Snapshot(repository, 1)]);

    public Task<GitHubReviewEvidence> GetReviewEvidenceAsync(GitHubRepository repository, PullRequestSnapshot snapshot, CancellationToken cancellationToken)
        => Task.FromResult(new GitHubReviewEvidence(snapshot.HeadSha, snapshot.BaseSha, "evidence", "hash", true));

    private static PullRequestSnapshot Snapshot(GitHubRepository repository, int number) => new(
        number, "Title", $"https://github.com/{repository.Owner}/{repository.Name}/pull/{number}", true, false,
        new string('a', 40), new string('b', 40), null, new string('a', 40), [], true,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "revision-1", "ci-revision-1", repository.RepositoryId, "main");
}
