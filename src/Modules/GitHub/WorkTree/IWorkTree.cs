namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// A registered local Git repository entry from the host's repository allowlist (<c>~/.intochat/repos.json</c> or configuration).
/// </summary>
/// <param name="Id">Short repository alias (e.g. <c>"os"</c> or <c>"intochat/os"</c>).</param>
/// <param name="LocalPath">Canonical absolute path to the source Git repository on disk.</param>
/// <param name="GitHubRepo">Optional GitHub <c>owner/repo</c> slug (auto-detected from <c>origin</c> URL when omitted).</param>
/// <param name="DefaultBaseBranch">Optional configured default base branch (auto-detected from remote HEAD when omitted).</param>
public sealed record RegisteredRepository(
    string Id,
    string LocalPath,
    string? GitHubRepo = null,
    string? DefaultBaseBranch = null);

/// <summary>
/// An active isolated Git worktree lease created for a single Feature-to-PR run, carrying the Triple Git Lock (R10) anchor.
/// </summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="RepositoryId">Registered repository alias.</param>
/// <param name="SourceRepoPath">Canonical path to the registered source repository.</param>
/// <param name="TreePath">Canonical path to the isolated external worktree (<c>&lt;Home&gt;/worktrees/&lt;repo&gt;/&lt;runId&gt;</c>).</param>
/// <param name="RealGitDirPath">Canonical path to the worktree's real <c>gitdir</c> inside the source repository's <c>.git/worktrees/</c> directory.</param>
/// <param name="GitPointerSha256Hex">Lowercase hex SHA-256 of the exact bytes of <c>&lt;TreePath&gt;/.git</c> recorded prior to untrusted editing.</param>
/// <param name="BaseBranch">Resolved base branch name (e.g. <c>main</c> or <c>refactor/ddd-clean-os</c>).</param>
/// <param name="BaseCommitSha">Exact 40-char commit SHA from which the worktree started.</param>
/// <param name="BranchName">Target branch name (always prefixed with <c>ino/</c>, e.g. <c>ino/fix-typo-a1b2c3d4</c>).</param>
/// <param name="GitHubRepo">Resolved GitHub <c>owner/repo</c> slug if <c>origin</c> is a GitHub remote; <see langword="null"/> for local bare remotes.</param>
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
    string? GitHubRepo = null);

/// <summary>
/// Verified single-commit output produced by <see cref="IWorkTree.VerifyAndCommitAsync"/> after passing Triple Git Lock and deterministic C# path gates.
/// </summary>
/// <param name="CommitSha">Exact 40-char commit SHA created on <see cref="WorkTreeLease.BranchName"/>.</param>
/// <param name="ChangedFiles">Sorted repo-relative forward-slash paths of changed files (1..5).</param>
/// <param name="HostRenderedDiff">Trusted unified <c>git diff</c> rendered by the host.</param>
/// <param name="Additions">Total added lines.</param>
/// <param name="Deletions">Total deleted lines.</param>
public sealed record WorkTreeCommit(
    string CommitSha,
    IReadOnlyList<string> ChangedFiles,
    string HostRenderedDiff,
    int Additions,
    int Deletions);

/// <summary>
/// Result of pushing a Person-approved commit SHA and opening a Draft Pull Request (or pushing to a local bare remote).
/// </summary>
/// <param name="CommitSha">The pushed commit SHA.</param>
/// <param name="BranchName">The pushed branch name (<c>ino/...</c>).</param>
/// <param name="PushedRef">Full remote ref pushed (<c>refs/heads/ino/...</c>).</param>
/// <param name="PullRequestUrl">Draft PR URL when <c>origin</c> is a GitHub remote; <see langword="null"/> when tested against a local bare remote.</param>
public sealed record PublishedPrResult(
    string CommitSha,
    string BranchName,
    string PushedRef,
    string? PullRequestUrl);

/// <summary>
/// Result of an <see cref="ICoder.EditAsync"/> coding pass inside an isolated worktree.
/// </summary>
/// <param name="Summary">Short human-readable summary of the edits applied.</param>
/// <param name="TouchedFiles">Repo-relative paths reported by the coder (still independently re-verified by <see cref="IWorkTree.VerifyAndCommitAsync"/>).</param>
/// <param name="ModelId">Identifier of the local or configured model/tool that performed the edit.</param>
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
/// Host-enforced Git worktree isolation, Triple Git Lock (R10), deterministic path gate, single-commit verification, and Person-gated Draft PR publisher.
/// </summary>
public interface IWorkTree
{
    /// <summary>Lists all allowlisted repositories configured on this host.</summary>
    IReadOnlyList<RegisteredRepository> ListRepositories();

    /// <summary>Registers or updates an allowlisted repository programmatically (e.g. in tests or host bootstrap).</summary>
    void RegisterRepository(RegisteredRepository repository);

    /// <summary>
    /// Creates an external detached worktree at <c>&lt;Home&gt;/worktrees/&lt;repo&gt;/&lt;runId&gt;</c> on branch <c>ino/&lt;slug&gt;-&lt;shortRunId&gt;</c>
    /// without ever modifying the source checkout or its stashes, and records the Triple Git Lock (R10) anchor.
    /// </summary>
    Task<WorkTreeLease> PrepareAsync(
        string repositoryId,
        string runId,
        string goal,
        string? baseBranch = null,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the Triple Git Lock (<c>.git</c> pointer file bytes unchanged), runs the deterministic C# path/extension/count/symlink gate,
    /// stages and creates exactly 1 commit authored by <c>Ino &lt;ino@intochat.ai&gt;</c>, and renders the host unified diff.
    /// </summary>
    Task<WorkTreeCommit> VerifyAndCommitAsync(
        WorkTreeLease lease,
        string commitMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Re-verifies Triple Git Lock, <c>HEAD == approvedSha</c>, and <c>1</c> commit ahead of base, pushes <c>&lt;approvedSha&gt;:refs/heads/&lt;branch&gt;</c>
    /// to <c>origin</c>, opens a Draft Pull Request via <c>gh pr create --draft</c> from an empty temporary directory (when GitHub remote),
    /// and cleans up the worktree.
    /// </summary>
    Task<PublishedPrResult> PublishAsync(
        WorkTreeLease lease,
        string approvedSha,
        string prTitle,
        string prBody,
        CancellationToken ct = default);

    /// <summary>Removes the external worktree and deletes its directory on disk without touching the source checkout.</summary>
    Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default);
}
