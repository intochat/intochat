using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Log;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Options controlling <see cref="WorkTreeService"/> execution backend, home/volume directories, repository allowlist path, policy, and CLI timeouts.
/// </summary>
public sealed class WorkTreeOptions
{
    public string? HomeDirectory { get; set; }

    public string? VolumeMountRoot { get; set; }

    public WorkTreeBackendKind Backend { get; set; } = WorkTreeBackendKind.LocalGit;

    public string? ReposConfigPath { get; set; }

    public int MaxChangedFiles
    {
        get => Policy.MaxChangedFiles;
        set => Policy = new ChangePolicy(value, Policy.AllowedExtensions, Policy.BlockedFileNames);
    }

    public ChangePolicy Policy { get; set; } = ChangePolicy.Default;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public bool VerifyFormatting { get; set; } = true;

    public string? ToolPath { get; set; }
}

/// <summary>
/// Application service coordinating <see cref="RepositoryAllowlist"/>, <see cref="GitCommandRunner"/>,
/// container/sandbox volume mirrors, N-parallel worktrees, Triple Git Lock verification, and <see cref="ChangePolicy"/> enforcement.
/// </summary>
public sealed partial class WorkTreeService : IWorkTree
{
    public const string AuthorName = "Ino";
    public const string AuthorEmail = "ino@intochat.io";

    private static readonly WorkTreeOptions DefaultOptions = new();
    private readonly WorkTreeOptions _options;
    private readonly ILogger<WorkTreeService> _logger;
    private readonly RepositoryAllowlist _allowlist;
    private readonly GitCommandRunner _git;
    private readonly SandboxVolumeHost _volumes;

    public WorkTreeService(
        IConfiguration? configuration = null,
        WorkTreeOptions? options = null,
        ILogger<WorkTreeService>? logger = null)
    {
        _options = options ?? DefaultOptions;
        _logger = logger ?? OsConsoleLog.For<WorkTreeService>();
        _allowlist = new RepositoryAllowlist(configuration, _options, _logger);
        _git = new GitCommandRunner(_options, _logger);
        _volumes = new SandboxVolumeHost(_options, _logger);
    }

    public string HomeDirectory => _allowlist.HomeDirectory;

    public WorkTreeBackendKind Backend => _options.Backend;

    public void RegisterRepository(RegisteredRepository repository) => _allowlist.Register(repository);

    public IReadOnlyList<RegisteredRepository> ListRepositories() => _allowlist.List();

    public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => _allowlist.Resolve(repositoryId);

    public async Task<WorkTreeLease> PrepareAsync(
        RepositoryId repositoryId,
        string runId,
        string goal,
        BranchName? baseBranch = null,
        CancellationToken ct = default)
    {
        var leases = await PrepareParallelAsync(repositoryId, runId, goal, instanceCount: 1, baseBranch, ct).ConfigureAwait(false);
        return leases[0];
    }

    public async Task<IReadOnlyList<WorkTreeLease>> PrepareParallelAsync(
        RepositoryId repositoryId,
        string runId,
        string goal,
        int instanceCount,
        BranchName? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(instanceCount, 0);

        var repo = ResolveRepository(repositoryId);
        var sourcePath = _options.Backend == WorkTreeBackendKind.ContainerVolume
            ? await _volumes.EnsureMirrorAsync(repo, HomeDirectory, _git, ct).ConfigureAwait(false)
            : ResolveLocalSourcePath(repo);

        var gate = _volumes.GateFor(sourcePath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var created = new List<WorkTreeLease>(instanceCount);
        try
        {
            var originUrl = await TryGetOriginUrlAsync(sourcePath, ct).ConfigureAwait(false);
            var pushUrl = await TryGetOriginUrlAsync(sourcePath, ct, push: true).ConfigureAwait(false);

            var requestedBase = baseBranch ?? (string.IsNullOrWhiteSpace(repo.DefaultBaseBranch)
                ? null
                : BranchName.FromRequestedBase(repo.DefaultBaseBranch));

            string? githubRepo;
            BranchName resolvedBaseBranch;
            CommitSha baseSha;

            if (repo.LocalOnly)
            {
                githubRepo = repo.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
                var (localBaseBranch, localStartRef) = await ResolveLocalBaseBranchAsync(sourcePath, requestedBase, ct).ConfigureAwait(false);
                resolvedBaseBranch = localBaseBranch;

                var localBaseShaResult = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-parse", "--verify", $"{localStartRef}^{{commit}}").ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(localBaseShaResult, $"resolve local base commit '{localStartRef}'");
                baseSha = CommitSha.Parse(localBaseShaResult.StdOut);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(originUrl) || string.IsNullOrWhiteSpace(pushUrl))
                {
                    throw new InvalidOperationException($"Repository '{repo.Id}' has no 'origin' remote configured.");
                }

                githubRepo = repo.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
                var allowLocalFileTransport = IsLocalFilesystemRemote(originUrl);

                var fetch = await _git.RunSourceGitAsync(sourcePath, allowLocalFileTransport, ct, "fetch", "origin", "--prune", "--no-tags").ConfigureAwait(false);
                if (fetch.ExitCode != 0)
                {
                    _logger.LogWarning("Non-fatal git fetch warning on {Repo}: {StdErr}", repo.Id, fetch.StdErr);
                }

                var (remoteBaseBranch, startRef) = await ResolveRemoteBaseBranchAsync(sourcePath, requestedBase, ct).ConfigureAwait(false);
                resolvedBaseBranch = remoteBaseBranch;

                var baseShaResult = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-parse", "--verify", $"{startRef}^{{commit}}").ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(baseShaResult, $"resolve base commit '{startRef}'");
                baseSha = CommitSha.Parse(baseShaResult.StdOut);

                var unpushed = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-list", "--count", baseSha.Value, "--not", "--remotes=origin").ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(unpushed, $"compare base '{resolvedBaseBranch.Value}' with origin");
                if (unpushed.StdOut.Trim() != "0")
                {
                    throw new SecurityException(
                        $"Refusing to start from base '{resolvedBaseBranch.Value}': it has {unpushed.StdOut.Trim()} commit(s) the remote does not have. Push the base first or name a branch that is on the remote.");
                }
            }

            _ = await _git.RunSourceGitAsync(sourcePath, false, ct, "worktree", "prune").ConfigureAwait(false);

            var safeRepoDir = BranchName.SanitizeSegment(repo.Id);
            var safeRunId = BranchName.SanitizeSegment(runId);
            var sanitizedPushUrl = WithoutCredentials(pushUrl);

            for (var i = 0; i < instanceCount; i++)
            {
                var leaseId = WorkTreeLease.BuildLeaseId(repo.Id, runId, i);
                var branch = instanceCount == 1
                    ? BranchName.CreateFeatureBranch(goal, runId)
                    : BranchName.CreateParallelFeatureBranch(goal, runId, i);

                string treePath;
                string? volumeId;
                if (_options.Backend == WorkTreeBackendKind.ContainerVolume)
                {
                    var leaseRoot = _volumes.GetLeaseRoot(HomeDirectory, leaseId);
                    treePath = Path.Combine(leaseRoot, "workspace");
                    volumeId = "vol-" + leaseId;
                }
                else
                {
                    var runSegment = instanceCount == 1
                        ? safeRunId
                        : string.Create(CultureInfo.InvariantCulture, $"{safeRunId}-w{i + 1}");
                    treePath = Path.GetFullPath(Path.Combine(HomeDirectory, "worktrees", safeRepoDir, runSegment));
                    volumeId = null;
                }

                if (Directory.Exists(treePath))
                {
                    await RemoveWorktreeDirectoryAsync(sourcePath, treePath, ct).ConfigureAwait(false);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(treePath)!);

                var addResult = await _git.RunSourceGitAsync(
                    sourcePath,
                    false,
                    ct,
                    "worktree",
                    "add",
                    "--no-track",
                    "-B",
                    branch.Value,
                    treePath,
                    baseSha.Value).ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(addResult, $"create isolated worktree at '{treePath}'");

                var dotGitFile = Path.Combine(treePath, ".git");
                var attr = File.GetAttributes(dotGitFile);
                if ((attr & FileAttributes.Directory) != 0 || (attr & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SecurityException($"Expected '{dotGitFile}' to be a regular gitdir pointer file.");
                }

                var pointerBytes = await File.ReadAllBytesAsync(dotGitFile, ct).ConfigureAwait(false);
                var pointerHashHex = Convert.ToHexStringLower(SHA256.HashData(pointerBytes));
                var realGitDir = ParseRealGitDirPath(treePath, pointerBytes);
                LockDotGitPointer(dotGitFile, attr);

                var instanceRunId = instanceCount == 1
                    ? runId
                    : string.Create(CultureInfo.InvariantCulture, $"{runId}-w{i + 1}");

                created.Add(new WorkTreeLease(
                    RunId: instanceRunId,
                    RepositoryId: repo.Id,
                    SourceRepoPath: sourcePath,
                    TreePath: treePath,
                    RealGitDirPath: realGitDir,
                    GitPointerSha256Hex: pointerHashHex,
                    BaseBranch: resolvedBaseBranch.Value,
                    BaseCommitSha: baseSha.Value,
                    BranchName: branch.Value,
                    GitHubRepo: githubRepo,
                    OriginUrl: sanitizedPushUrl,
                    LeaseId: leaseId,
                    InstanceIndex: i,
                    Backend: _options.Backend,
                    VolumeId: volumeId));
            }

            return created;
        }
        catch
        {
            foreach (var lease in created)
            {
                await RemoveWorktreeDirectoryAsync(lease.SourceRepoPath, lease.TreePath, CancellationToken.None).ConfigureAwait(false);
                _volumes.CleanupLeaseVolumeDirectory(lease);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ResolveLocalSourcePath(RegisteredRepository repo)
    {
        var sourcePath = Path.GetFullPath(repo.LocalPath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Registered repository '{repo.Id}' path does not exist: {sourcePath}");
        }

        return sourcePath;
    }

    public async Task<ChangeSet> VerifyAndCommitAsync(
        WorkTreeLease lease,
        string commitMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var headCheck = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(headCheck, "read worktree HEAD");
        var currentHead = CommitSha.Parse(headCheck.StdOut);

        var statusResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "status", "-z", "--porcelain=v1", "--no-renames", "-uall").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(statusResult, "inspect worktree status");

        var rawChangedPaths = ParsePorcelainZPaths(statusResult.StdOut);
        var baseSha = CommitSha.Parse(lease.BaseCommitSha);

        // Idempotent recovery: if step already committed 1 commit ahead of BaseCommitSha and working tree is clean, verify & return it.
        if (rawChangedPaths.Count == 0 && currentHead != baseSha)
        {
            await EnsureSingleCommitAheadAsync(lease, currentHead, ct).ConfigureAwait(false);
            var existingFilesResult = await _git.RunLockedGitAsync(
                lease,
                false,
                ct,
                "diff", "--name-only", "-z", "--no-renames", $"{baseSha.Value}..{currentHead.Value}").ConfigureAwait(false);
            GitCommandRunner.EnsureSuccess(existingFilesResult, "list committed files");
            var existingRaw = existingFilesResult.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var verifiedExisting = ResolvePolicy(lease).Enforce(lease.TreePath, existingRaw);
            await _git.VerifyCSharpFormattingAsync(lease.TreePath, verifiedExisting, ct).ConfigureAwait(false);
            var existingDiff = await RenderUnifiedDiffAsync(lease, currentHead, ct).ConfigureAwait(false);
            return ChangeSet.FromDiff(currentHead, verifiedExisting, existingDiff);
        }

        if (currentHead != baseSha)
        {
            throw new SecurityException(
                $"Worktree HEAD ('{currentHead.Value}') diverged from base commit ('{baseSha.Value}') before host commit.");
        }

        if (rawChangedPaths.Count == 0)
        {
            throw new InvalidOperationException("Coder produced zero file modifications in the worktree.");
        }

        var changedPaths = ResolvePolicy(lease).Enforce(lease.TreePath, rawChangedPaths);
        await _git.VerifyCSharpFormattingAsync(lease.TreePath, changedPaths, ct).ConfigureAwait(false);

        var pathArgs = changedPaths.Select(static p => p.Value).ToArray();
        var stageResult = await _git.RunLockedGitAsync(lease, false, ct, ["add", "-A", "--", .. pathArgs]).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(stageResult, "stage validated files");

        var lsStage = await _git.RunLockedGitAsync(lease, false, ct, ["ls-files", "--stage", "-z", "--", .. pathArgs]).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(lsStage, "verify staged git index modes");
        VerifyIndexModes(lsStage.StdOut);

        var cleanMessage = ChangePolicy.SanitizeCommitText(commitMessage);
        if (cleanMessage.Length == 0)
        {
            cleanMessage = "chore(ino): automated change";
        }

        var commitResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "-c", $"user.name={AuthorName}",
            "-c", $"user.email={AuthorEmail}",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", cleanMessage).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(commitResult, "create single worktree commit");

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var shaResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(shaResult, "read committed SHA");
        var commitSha = CommitSha.Parse(shaResult.StdOut);

        await EnsureSingleCommitAheadAsync(lease, commitSha, ct).ConfigureAwait(false);

        var diff = await RenderUnifiedDiffAsync(lease, commitSha, ct).ConfigureAwait(false);
        return ChangeSet.FromDiff(commitSha, changedPaths, diff);
    }

    public async Task<ChangeSet> VerifyAndAmendAsync(
        WorkTreeLease lease,
        CommitSha expectedPreviousHead,
        string commitMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(expectedPreviousHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var headCheck = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(headCheck, "read worktree HEAD");
        var currentHead = CommitSha.Parse(headCheck.StdOut);
        if (currentHead != expectedPreviousHead)
        {
            throw new SecurityException(
                $"Worktree HEAD ('{currentHead.Value}') does not match expected prior host commit ('{expectedPreviousHead.Value}').");
        }

        await EnsureSingleCommitAheadAsync(lease, currentHead, ct).ConfigureAwait(false);

        var baseSha = CommitSha.Parse(lease.BaseCommitSha);
        var statusResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "status", "-z", "--porcelain=v1", "--no-renames", "-uall").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(statusResult, "inspect worktree status");
        var rawChangedPaths = ParsePorcelainZPaths(statusResult.StdOut);

        var existingFilesResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "diff", "--name-only", "-z", "--no-renames", $"{baseSha.Value}..{currentHead.Value}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(existingFilesResult, "list committed files");
        var existingRaw = existingFilesResult.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        if (rawChangedPaths.Count == 0)
        {
            var verifiedExisting = ResolvePolicy(lease).Enforce(lease.TreePath, existingRaw);
            await _git.VerifyCSharpFormattingAsync(lease.TreePath, verifiedExisting, ct).ConfigureAwait(false);
            var existingDiff = await RenderUnifiedDiffAsync(lease, currentHead, ct).ConfigureAwait(false);
            return ChangeSet.FromDiff(currentHead, verifiedExisting, existingDiff);
        }

        var policy = ResolvePolicy(lease);
        var combinedRaw = existingRaw
            .Concat(rawChangedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ = policy.Enforce(lease.TreePath, combinedRaw);

        var dirtyPaths = policy.Enforce(lease.TreePath, rawChangedPaths);
        await _git.VerifyCSharpFormattingAsync(lease.TreePath, dirtyPaths, ct).ConfigureAwait(false);

        var pathArgs = dirtyPaths.Select(static p => p.Value).ToArray();
        var stageResult = await _git.RunLockedGitAsync(lease, false, ct, ["add", "-A", "--", .. pathArgs]).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(stageResult, "stage repaired files");

        var lsStage = await _git.RunLockedGitAsync(lease, false, ct, ["ls-files", "--stage", "-z", "--", .. pathArgs]).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(lsStage, "verify staged git index modes");
        VerifyIndexModes(lsStage.StdOut);

        var cleanMessage = ChangePolicy.SanitizeCommitText(commitMessage);
        if (cleanMessage.Length == 0)
        {
            cleanMessage = "chore(ino): automated change";
        }

        var amendResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "-c", $"user.name={AuthorName}",
            "-c", $"user.email={AuthorEmail}",
            "-c", "commit.gpgsign=false",
            "commit", "--amend", "--no-verify", "-m", cleanMessage).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(amendResult, "amend single worktree commit");

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var shaResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(shaResult, "read amended commit SHA");
        var commitSha = CommitSha.Parse(shaResult.StdOut);

        await EnsureSingleCommitAheadAsync(lease, commitSha, ct).ConfigureAwait(false);

        var finalFilesResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "diff", "--name-only", "-z", "--no-renames", $"{baseSha.Value}..{commitSha.Value}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(finalFilesResult, "list amended committed files");
        var finalRaw = finalFilesResult.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var finalPaths = policy.Enforce(lease.TreePath, finalRaw);
        var diff = await RenderUnifiedDiffAsync(lease, commitSha, ct).ConfigureAwait(false);
        return ChangeSet.FromDiff(commitSha, finalPaths, diff);
    }

    private ChangePolicy ResolvePolicy(WorkTreeLease lease)
        => ReferenceEquals(_options.Policy, ChangePolicy.Default)
            ? ChangePolicy.ForRepository(lease.RepositoryId)
            : _options.Policy;

    public async Task<string> PushAsync(
        WorkTreeLease lease,
        CommitSha approvedSha,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(approvedSha);

        var branch = BranchName.Parse(lease.BranchName);
        if (!branch.IsFeatureBranch)
        {
            throw new SecurityException($"Refusing to push branch '{branch.Value}': branch name must start with 'ino/'.");
        }

        var originUrl = await TryGetOriginUrlAsync(lease.SourceRepoPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            throw new InvalidOperationException($"Repository '{lease.RepositoryId}' has no 'origin' remote configured.");
        }

        var pushUrl = WithoutCredentials(await TryGetOriginUrlAsync(lease.SourceRepoPath, ct, push: true).ConfigureAwait(false));
        if (lease.OriginUrl is { } approvedUrl && !string.Equals(approvedUrl, pushUrl, StringComparison.Ordinal))
        {
            throw new SecurityException($"Refusing to push: origin now pushes to '{pushUrl}', but the approval showed '{approvedUrl}'.");
        }

        var allowLocalFileTransport = IsLocalFilesystemRemote(originUrl);
        var targetRef = branch.RemoteRef;

        // Idempotent check: if origin already holds this exact approved commit on targetRef, return without failing.
        var remoteCheck = await _git.RunSourceGitAsync(
            lease.SourceRepoPath,
            allowLocalFileTransport,
            ct,
            "ls-remote",
            "--heads",
            "origin",
            branch.Value).ConfigureAwait(false);
        if (remoteCheck.ExitCode == 0 && TryParseLsRemoteSha(remoteCheck.StdOut, targetRef, out var remoteSha))
        {
            if (remoteSha == approvedSha)
            {
                return targetRef;
            }

            throw new SecurityException(
                $"Refusing to overwrite remote branch '{targetRef}': origin already points to '{remoteSha.Value}', expected '{approvedSha.Value}'.");
        }

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var headResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(headResult, "verify HEAD before push");
        var headSha = CommitSha.Parse(headResult.StdOut);
        if (headSha != approvedSha)
        {
            throw new SecurityException(
                $"Approved commit SHA mismatch: person approved '{approvedSha.Value}', but worktree HEAD is '{headSha.Value}'.");
        }

        await EnsureSingleCommitAheadAsync(lease, headSha, ct).ConfigureAwait(false);
        await EnsureSingleUnpushedCommitAgainstOriginAsync(lease, headSha, ct).ConfigureAwait(false);

        var dirtyCheck = await _git.RunLockedGitAsync(lease, false, ct, "status", "--porcelain=v1", "-z", "-uall").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(dirtyCheck, "verify clean worktree before push");
        if (!string.IsNullOrEmpty(dirtyCheck.StdOut))
        {
            throw new SecurityException("Refusing to push: worktree contains uncommitted modifications after commit verification.");
        }

        var pushResult = await _git.RunLockedGitAsync(
            lease,
            allowLocalFileTransport: allowLocalFileTransport,
            ct,
            "push", "--porcelain", "--no-verify", "origin", $"{headSha.Value}:{targetRef}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(pushResult, $"push '{headSha.Value}:{targetRef}' to origin");

        return targetRef;
    }

    public async Task<string?> CreateDraftPullRequestAsync(
        WorkTreeLease lease,
        string prTitle,
        string prBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(prTitle);

        var originUrl = await TryGetOriginUrlAsync(lease.SourceRepoPath, ct).ConfigureAwait(false);
        var githubRepo = lease.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
        if (string.IsNullOrWhiteSpace(githubRepo))
        {
            return null;
        }

        var branch = BranchName.Parse(lease.BranchName);
        var baseBranch = BranchName.Parse(lease.BaseBranch);
        var cleanTitle = ChangePolicy.SanitizeCommitText(prTitle);
        if (cleanTitle.Length == 0)
        {
            cleanTitle = "Automated change";
        }

        // Idempotent check: reuse an already-created PR if a prior attempt crashed after `gh pr create`.
        var existingResult = await _git.RunGhFromEmptyDirAsync(
            ct,
            "pr", "list",
            "--repo", githubRepo,
            "--head", branch.Value,
            "--state", "all",
            "--json", "url",
            "--limit", "1").ConfigureAwait(false);
        if (existingResult.ExitCode == 0 && TryExtractExistingPrUrl(existingResult.StdOut) is { } existingUrl)
        {
            return existingUrl;
        }

        var createResult = await _git.RunGhFromEmptyDirAsync(
            ct,
            "pr", "create",
            "--draft",
            "--repo", githubRepo,
            "--head", branch.Value,
            "--base", baseBranch.Value,
            "--title", cleanTitle,
            "--body", prBody ?? string.Empty).ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(createResult, $"create draft PR on '{githubRepo}'");

        return createResult.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ?? createResult.StdOut.Trim();
    }

    public async Task<PublishedPrResult> PublishAsync(
        WorkTreeLease lease,
        CommitSha approvedSha,
        string prTitle,
        string prBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(approvedSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(prTitle);

        var pushedRef = await PushAsync(lease, approvedSha, ct).ConfigureAwait(false);
        var prUrl = await CreateDraftPullRequestAsync(lease, prTitle, prBody, ct).ConfigureAwait(false);
        await CleanupAsync(lease, ct).ConfigureAwait(false);

        return new PublishedPrResult(
            Commit: approvedSha,
            Branch: BranchName.Parse(lease.BranchName),
            PushedRef: pushedRef,
            PullRequestUrl: prUrl);
    }

    public async Task<CommitSha> MergeLocalAsync(
        WorkTreeLease lease,
        CommitSha approvedSha,
        string targetBranch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(approvedSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);

        var branch = BranchName.Parse(lease.BranchName);
        if (!branch.IsFeatureBranch)
        {
            throw new SecurityException($"Refusing to merge branch '{branch.Value}': branch name must start with 'ino/'.");
        }

        var parsedTarget = BranchName.Parse(targetBranch);

        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var headResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(headResult, "verify HEAD before local merge");
        var headSha = CommitSha.Parse(headResult.StdOut);
        if (headSha != approvedSha)
        {
            throw new SecurityException(
                $"Approved commit SHA mismatch: person approved '{approvedSha.Value}', but worktree HEAD is '{headSha.Value}'.");
        }

        var dirtyCheck = await _git.RunLockedGitAsync(lease, false, ct, "status", "--porcelain=v1", "-z", "-uall").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(dirtyCheck, "verify clean worktree before local merge");
        if (!string.IsNullOrEmpty(dirtyCheck.StdOut))
        {
            throw new SecurityException("Refusing to merge locally: worktree contains uncommitted modifications after commit verification.");
        }

        var gate = _volumes.GateFor(lease.SourceRepoPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targetRef = $"refs/heads/{parsedTarget.Value}";
            var oldTargetResult = await _git.RunSourceGitAsync(lease.SourceRepoPath, false, ct, "rev-parse", "--verify", $"{targetRef}^{{commit}}").ConfigureAwait(false);
            GitCommandRunner.EnsureSuccess(oldTargetResult, $"resolve local target branch '{parsedTarget.Value}'");
            var oldTargetSha = CommitSha.Parse(oldTargetResult.StdOut);

            if (oldTargetSha == approvedSha)
            {
                return approvedSha;
            }

            var ancestorCheck = await _git.RunSourceGitAsync(
                lease.SourceRepoPath,
                false,
                ct,
                "merge-base",
                "--is-ancestor",
                oldTargetSha.Value,
                approvedSha.Value).ConfigureAwait(false);
            if (ancestorCheck.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing non-fast-forward local merge: target branch '{parsedTarget.Value}' ('{oldTargetSha.Value}') is not an ancestor of '{approvedSha.Value}'.");
            }

            var currentBranchResult = await _git.RunSourceGitAsync(lease.SourceRepoPath, false, ct, "symbolic-ref", "-q", "HEAD").ConfigureAwait(false);
            var currentHeadRef = currentBranchResult.ExitCode == 0 ? currentBranchResult.StdOut.Trim() : string.Empty;

            if (string.Equals(currentHeadRef, targetRef, StringComparison.Ordinal))
            {
                var mergeResult = await _git.RunSourceGitAsync(lease.SourceRepoPath, false, ct, "merge", "--ff-only", approvedSha.Value).ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(mergeResult, $"fast-forward checked-out branch '{parsedTarget.Value}' to '{approvedSha.Value}'");
            }
            else
            {
                var updateResult = await _git.RunSourceGitAsync(
                    lease.SourceRepoPath,
                    false,
                    ct,
                    "update-ref",
                    targetRef,
                    approvedSha.Value,
                    oldTargetSha.Value).ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(updateResult, $"fast-forward branch '{parsedTarget.Value}' to '{approvedSha.Value}'");
            }

            return approvedSha;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var gate = _volumes.GateFor(lease.SourceRepoPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RemoveWorktreeDirectoryAsync(lease.SourceRepoPath, lease.TreePath, ct).ConfigureAwait(false);
            _volumes.CleanupLeaseVolumeDirectory(lease);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CleanupParallelAsync(IReadOnlyList<WorkTreeLease> leases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leases);
        foreach (var lease in leases)
        {
            await CleanupAsync(lease, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies Triple Git Lock (R10): <c>&lt;TreePath&gt;/.git</c> exists as a regular file,
    /// matches <see cref="WorkTreeLease.GitPointerSha256Hex"/> in constant time, and resolves to <see cref="WorkTreeLease.RealGitDirPath"/>.
    /// </summary>
    public static async Task VerifyTripleGitLockAsync(WorkTreeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var dotGitPath = Path.Combine(lease.TreePath, ".git");
        if (!File.Exists(dotGitPath))
        {
            if (Directory.Exists(dotGitPath))
            {
                throw new SecurityException($"Triple Git Lock violation: '{dotGitPath}' was replaced with a directory.");
            }

            throw new SecurityException($"Triple Git Lock violation: '{dotGitPath}' is missing.");
        }

        var attributes = File.GetAttributes(dotGitPath);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException($"Triple Git Lock violation: '{dotGitPath}' is a directory or reparse point/symlink.");
        }

        var currentBytes = await File.ReadAllBytesAsync(dotGitPath, ct).ConfigureAwait(false);
        var currentHash = SHA256.HashData(currentBytes);
        var expectedHash = Convert.FromHexString(lease.GitPointerSha256Hex);

        if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
        {
            throw new SecurityException(
                $"Triple Git Lock violation: '{dotGitPath}' bytes were modified during untrusted execution.");
        }

        var resolvedGitDir = ParseRealGitDirPath(lease.TreePath, currentBytes);
        if (!PathsEqual(resolvedGitDir, lease.RealGitDirPath))
        {
            throw new SecurityException(
                $"Triple Git Lock violation: '{dotGitPath}' points to '{resolvedGitDir}' instead of '{lease.RealGitDirPath}'.");
        }
    }

    /// <summary>
    /// The remote address without the user name, password or token a URL can carry before its host.
    /// A remote with several push addresses gives one per line.
    /// </summary>
    public static string? WithoutCredentials(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        return string.Join('\n', remoteUrl
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static url =>
            {
                var scheme = url.IndexOf("://", StringComparison.Ordinal);
                if (scheme < 0)
                {
                    return url;
                }

                var hostStart = scheme + 3;
                var pathStart = url.IndexOfAny(['/', '?', '#'], hostStart);
                var at = (pathStart < 0 ? url[hostStart..] : url[hostStart..pathStart]).LastIndexOf('@');
                return at < 0 ? url : url[..hostStart] + url[(hostStart + at + 1)..];
            }));
    }
}
