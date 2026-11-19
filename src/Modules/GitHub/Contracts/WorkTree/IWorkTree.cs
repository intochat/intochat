using System.Globalization;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

public enum WorkTreeBackendKind
{
    LocalGit = 0,
    ContainerVolume = 1,
}

/// <summary>
/// A registered Git repository entry from the host's repository allowlist (<c>~/.intochat/repos.json</c> or configuration).
/// </summary>
public sealed record RegisteredRepository(
    string Id,
    string LocalPath,
    string? GitHubRepo = null,
    string? DefaultBaseBranch = null,
    string? RemoteCloneUrl = null);

/// <summary>
/// An active isolated Git worktree lease created for a single or N-parallel engineering run, carrying the Triple Git Lock (R10) anchor.
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
    string? OriginUrl = null,
    string? LeaseId = null,
    int InstanceIndex = 0,
    WorkTreeBackendKind Backend = WorkTreeBackendKind.LocalGit,
    string? VolumeId = null)
{
    [JsonIgnore]
    public string EffectiveLeaseId => string.IsNullOrWhiteSpace(LeaseId)
        ? BuildLeaseId(RepositoryId, RunId, InstanceIndex)
        : LeaseId;

    public static string BuildLeaseId(string repositoryId, string runId, int instanceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceIndex);
        var safeRepo = WorkTree.BranchName.SanitizeSegment(string.IsNullOrWhiteSpace(repositoryId) ? "repo" : repositoryId);
        var safeRun = WorkTree.BranchName.SanitizeSegment(string.IsNullOrWhiteSpace(runId) ? "run" : runId);
        return string.Create(CultureInfo.InvariantCulture, $"{safeRepo}-{safeRun}-{instanceIndex}");
    }
}

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
/// Host-enforced Git worktree isolation (across local checkouts and container/sandbox volumes), Triple Git Lock (R10),
/// <see cref="ChangePolicy"/> gate, whitespace format verification, single-commit creation, and checkpointed idempotent publish steps.
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

    async Task<IReadOnlyList<WorkTreeLease>> PrepareParallelAsync(
        RepositoryId repositoryId,
        string runId,
        string goal,
        int instanceCount,
        BranchName? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(instanceCount, 0);
        if (instanceCount == 1)
        {
            return [await PrepareAsync(repositoryId, runId, goal, baseBranch, ct).ConfigureAwait(false)];
        }

        var leases = new List<WorkTreeLease>(instanceCount);
        try
        {
            for (var i = 0; i < instanceCount; i++)
            {
                var shardRunId = string.Create(CultureInfo.InvariantCulture, $"w{i + 1}-{runId}");
                var prepared = await PrepareAsync(repositoryId, shardRunId, goal, baseBranch, ct).ConfigureAwait(false);
                leases.Add(prepared with
                {
                    LeaseId = WorkTreeLease.BuildLeaseId(prepared.RepositoryId, runId, i),
                    InstanceIndex = i,
                });
            }

            return leases;
        }
        catch
        {
            foreach (var lease in leases)
            {
                await CleanupAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

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

    async Task CleanupParallelAsync(IReadOnlyList<WorkTreeLease> leases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leases);
        foreach (var lease in leases)
        {
            await CleanupAsync(lease, ct).ConfigureAwait(false);
        }
    }
}
