namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// A registered local Git repository entry from the host's repository allowlist (<c>~/.intochat/repos.json</c> or configuration).
/// </summary>
public sealed record RegisteredRepository(
    string Id,
    string LocalPath,
    string? GitHubRepo = null,
    string? DefaultBaseBranch = null);

/// <summary>
/// An active isolated Git worktree lease created for a single Feature-to-PR run, carrying the Triple Git Lock (R10) anchor.
/// </summary>
public sealed record WorkTreeLease(
    string RunId,
    string RepositoryId,
    string SourceRepoPath,
    string TreePath,
    string RealGitDirPath,
    string GitPointerSha256Hex,
    string BaseBranch,
    string BaseCommitSha,
    string BranchName,
    string? GitHubRepo = null,
    string? OriginUrl = null);

/// <summary>
/// Result of pushing a Person-approved commit SHA and opening a Draft Pull Request (or pushing to a local bare remote).
/// </summary>
public sealed record PublishedPrResult(
    CommitSha Commit,
    BranchName Branch,
    string PushedRef,
    string? PullRequestUrl);

/// <summary>
/// Result of an <see cref="ICoder.EditAsync"/> coding pass inside an isolated worktree.
/// </summary>
public sealed record CoderEditResult(
    string Summary,
    IReadOnlyList<string> TouchedFiles,
    string ModelId);

/// <summary>
/// Pluggable coding capability invoked between <see cref="IWorkTree.PrepareAsync"/> and <see cref="IWorkTree.VerifyAndCommitAsync"/>.
/// </summary>
public interface ICoder
{
    Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default);
}

/// <summary>
/// Host-enforced Git worktree isolation, Triple Git Lock (R10), <see cref="ChangePolicy"/> gate,
/// whitespace format verification, single-commit creation, and checkpointed idempotent publish steps.
/// </summary>
public interface IWorkTree
{
    IReadOnlyList<RegisteredRepository> ListRepositories();

    void RegisterRepository(RegisteredRepository repository);

    RegisteredRepository ResolveRepository(RepositoryId repositoryId);

    Task<WorkTreeLease> PrepareAsync(
        RepositoryId repositoryId,
        string runId,
        string goal,
        BranchName? baseBranch = null,
        CancellationToken ct = default);

    Task<ChangeSet> VerifyAndCommitAsync(
        WorkTreeLease lease,
        string commitMessage,
        CancellationToken ct = default);

    Task<string> PushAsync(
        WorkTreeLease lease,
        CommitSha approvedSha,
        CancellationToken ct = default);

    Task<string?> CreateDraftPullRequestAsync(
        WorkTreeLease lease,
        string prTitle,
        string prBody,
        CancellationToken ct = default);

    Task<PublishedPrResult> PublishAsync(
        WorkTreeLease lease,
        CommitSha approvedSha,
        string prTitle,
        string prBody,
        CancellationToken ct = default);

    Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default);
}
