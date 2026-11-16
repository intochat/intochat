using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Options controlling <see cref="WorkTreeService"/> home directory, repository allowlist path, policy, and CLI timeouts.
/// </summary>
public sealed class WorkTreeOptions
{
    public string? HomeDirectory { get; set; }

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
/// Triple Git Lock verification, and <see cref="ChangePolicy"/> enforcement.
/// </summary>
public sealed partial class WorkTreeService : IWorkTree
{
    public const string AuthorName = "Ino";
    public const string AuthorEmail = "ino@intochat.ai";

    private static readonly WorkTreeOptions DefaultOptions = new();
    private readonly WorkTreeOptions _options;
    private readonly ILogger<WorkTreeService> _logger;
    private readonly RepositoryAllowlist _allowlist;
    private readonly GitCommandRunner _git;

    public WorkTreeService(
        IConfiguration? configuration = null,
        WorkTreeOptions? options = null,
        ILogger<WorkTreeService>? logger = null)
    {
        _options = options ?? DefaultOptions;
        _logger = logger ?? OsConsoleLog.For<WorkTreeService>();
        _allowlist = new RepositoryAllowlist(configuration, _options, _logger);
        _git = new GitCommandRunner(_options, _logger);
    }

    public string HomeDirectory => _allowlist.HomeDirectory;

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
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var repo = ResolveRepository(repositoryId);
        var sourcePath = Path.GetFullPath(repo.LocalPath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Registered repository '{repo.Id}' path does not exist: {sourcePath}");
        }

        var safeRepoDir = BranchName.SanitizeSegment(repo.Id);
        var safeRunId = BranchName.SanitizeSegment(runId);
        var branch = BranchName.CreateFeatureBranch(goal, runId);

        var treePath = Path.GetFullPath(Path.Combine(HomeDirectory, "worktrees", safeRepoDir, safeRunId));
        if (Directory.Exists(treePath))
        {
            await RemoveWorktreeDirectoryAsync(sourcePath, treePath, ct).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(treePath)!);

        var originUrl = await TryGetOriginUrlAsync(sourcePath, ct).ConfigureAwait(false);
        var pushUrl = await TryGetOriginUrlAsync(sourcePath, ct, push: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(originUrl) || string.IsNullOrWhiteSpace(pushUrl))
        {
            throw new InvalidOperationException($"Repository '{repo.Id}' has no 'origin' remote configured.");
        }

        var githubRepo = repo.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
        var allowLocalFileTransport = IsLocalFilesystemRemote(originUrl);

        var fetch = await _git.RunSourceGitAsync(sourcePath, allowLocalFileTransport, ct, "fetch", "origin", "--prune", "--no-tags").ConfigureAwait(false);
        if (fetch.ExitCode != 0)
        {
            _logger.LogWarning("Non-fatal git fetch warning on {Repo}: {StdErr}", repo.Id, fetch.StdErr);
        }

        var requestedBase = baseBranch ?? (string.IsNullOrWhiteSpace(repo.DefaultBaseBranch)
            ? null
            : BranchName.FromRequestedBase(repo.DefaultBaseBranch));
        var (resolvedBaseBranch, startRef) = await ResolveRemoteBaseBranchAsync(sourcePath, requestedBase, ct).ConfigureAwait(false);

        var baseShaResult = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-parse", "--verify", $"{startRef}^{{commit}}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(baseShaResult, $"resolve base commit '{startRef}'");
        var baseSha = CommitSha.Parse(baseShaResult.StdOut);

        var unpushed = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-list", "--count", baseSha.Value, "--not", "--remotes=origin").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(unpushed, $"compare base '{resolvedBaseBranch.Value}' with origin");
        if (unpushed.StdOut.Trim() != "0")
        {
            throw new SecurityException(
                $"Refusing to start from base '{resolvedBaseBranch.Value}': it has {unpushed.StdOut.Trim()} commit(s) the remote does not have. Push the base first or name a branch that is on the remote.");
        }

        _ = await _git.RunSourceGitAsync(sourcePath, false, ct, "worktree", "prune").ConfigureAwait(false);

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

        return new WorkTreeLease(
            RunId: runId,
            RepositoryId: repo.Id,
            SourceRepoPath: sourcePath,
            TreePath: treePath,
            RealGitDirPath: realGitDir,
            GitPointerSha256Hex: pointerHashHex,
            BaseBranch: resolvedBaseBranch.Value,
            BaseCommitSha: baseSha.Value,
            BranchName: branch.Value,
            GitHubRepo: githubRepo,
            OriginUrl: WithoutCredentials(pushUrl));
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
            var verifiedExisting = _options.Policy.Enforce(lease.TreePath, existingRaw);
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

        var changedPaths = _options.Policy.Enforce(lease.TreePath, rawChangedPaths);
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

    public async Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await RemoveWorktreeDirectoryAsync(lease.SourceRepoPath, lease.TreePath, ct).ConfigureAwait(false);
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

    private async Task<(BranchName BaseBranch, string StartRef)> ResolveRemoteBaseBranchAsync(
        string sourcePath,
        BranchName? requestedBranch,
        CancellationToken ct)
    {
        if (requestedBranch is not null)
        {
            var remoteRef = $"refs/remotes/origin/{requestedBranch.Value}";
            if (await RefExistsAsync(sourcePath, remoteRef, ct).ConfigureAwait(false))
            {
                return (requestedBranch, remoteRef);
            }

            // F-12: strict single base rule — never fall back to local branches or HEAD when origin lacks the requested branch.
            throw new SecurityException(
                $"Refusing to start from base '{requestedBranch.Value}': the remote does not have branch '{requestedBranch.Value}'. Push the base first or name a branch that is on the remote.");
        }

        var sym = await _git.RunSourceGitAsync(sourcePath, false, ct, "symbolic-ref", "-q", "refs/remotes/origin/HEAD").ConfigureAwait(false);
        if (sym.ExitCode == 0 && sym.StdOut.Trim() is { Length: > 0 } symRef)
        {
            const string prefix = "refs/remotes/origin/";
            if (symRef.StartsWith(prefix, StringComparison.Ordinal)
                && await RefExistsAsync(sourcePath, symRef, ct).ConfigureAwait(false))
            {
                return (BranchName.Parse(symRef[prefix.Length..]), symRef);
            }
        }

        foreach (var candidate in new[] { "refactor/ddd-clean-os", "main", "master" })
        {
            var remoteRef = $"refs/remotes/origin/{candidate}";
            if (await RefExistsAsync(sourcePath, remoteRef, ct).ConfigureAwait(false))
            {
                return (BranchName.Parse(candidate), remoteRef);
            }
        }

        var headBranch = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (headBranch.ExitCode == 0 && headBranch.StdOut.Trim() is { Length: > 0 } b && b != "HEAD")
        {
            var remoteRef = $"refs/remotes/origin/{b}";
            if (await RefExistsAsync(sourcePath, remoteRef, ct).ConfigureAwait(false))
            {
                return (BranchName.Parse(b), remoteRef);
            }
        }

        throw new SecurityException("Refusing to start: origin has no remote tracking branches available as a base.");
    }

    private async Task<bool> RefExistsAsync(string sourcePath, string fullRef, CancellationToken ct)
    {
        var res = await _git.RunSourceGitAsync(sourcePath, false, ct, "show-ref", "--verify", "--quiet", fullRef).ConfigureAwait(false);
        return res.ExitCode == 0;
    }

    private async Task EnsureSingleCommitAheadAsync(WorkTreeLease lease, CommitSha commitSha, CancellationToken ct)
    {
        var countResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-list", "--count", $"{lease.BaseCommitSha}..{commitSha.Value}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(countResult, "verify commit count ahead of base");
        if (!int.TryParse(countResult.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != 1)
        {
            throw new SecurityException(
                $"Expected exactly 1 commit ahead of base '{lease.BaseCommitSha}', but found {countResult.StdOut.Trim()}.");
        }
    }

    private async Task EnsureSingleUnpushedCommitAgainstOriginAsync(WorkTreeLease lease, CommitSha commitSha, CancellationToken ct)
    {
        var unpushedResult = await _git.RunLockedGitAsync(lease, false, ct, "rev-list", "--count", commitSha.Value, "--not", "--remotes=origin").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(unpushedResult, "verify unpushed commit count against origin");
        if (!int.TryParse(unpushedResult.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != 1)
        {
            throw new SecurityException(
                $"Expected exactly 1 unpushed commit against origin, but found {unpushedResult.StdOut.Trim()}.");
        }
    }

    private async Task<string> RenderUnifiedDiffAsync(WorkTreeLease lease, CommitSha commitSha, CancellationToken ct)
    {
        var diffResult = await _git.RunLockedGitAsync(
            lease,
            false,
            ct,
            "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--unified=3", $"{lease.BaseCommitSha}..{commitSha.Value}").ConfigureAwait(false);
        GitCommandRunner.EnsureSuccess(diffResult, "render host unified diff");
        return diffResult.StdOut;
    }

    private async Task<string?> TryGetOriginUrlAsync(string sourcePath, CancellationToken ct, bool push = false)
    {
        string[] getUrl = push ? ["remote", "get-url", "--push", "--all", "origin"] : ["remote", "get-url", "origin"];
        var res = await _git.RunSourceGitAsync(sourcePath, false, ct, getUrl).ConfigureAwait(false);
        return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut) ? res.StdOut.Trim() : null;
    }

    private async Task RemoveWorktreeDirectoryAsync(string sourceRepoPath, string treePath, CancellationToken ct)
    {
        if (Directory.Exists(sourceRepoPath))
        {
            _ = await _git.RunSourceGitAsync(sourceRepoPath, false, ct, "worktree", "remove", "--force", treePath).ConfigureAwait(false);
            _ = await _git.RunSourceGitAsync(sourceRepoPath, false, ct, "worktree", "prune").ConfigureAwait(false);
        }

        if (Directory.Exists(treePath))
        {
            try
            {
                ClearReadOnlyAttributes(treePath);
                Directory.Delete(treePath, recursive: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "Best-effort directory delete during worktree cleanup.");
            }
        }
    }

    private void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException ex)
            {
                _logger.LogSwallowed(ex, "Best-effort clear ReadOnly attribute (IO).");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogSwallowed(ex, "Best-effort clear ReadOnly attribute (ACL).");
            }
        }
    }

    private static void LockDotGitPointer(string dotGitFile, FileAttributes currentAttributes)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(dotGitFile, currentAttributes | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(dotGitFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void VerifyIndexModes(string lsFilesStageZ)
    {
        foreach (var entry in lsFilesStageZ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = entry.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            var mode = entry[..space];
            if (mode is "120000")
            {
                throw new SecurityException($"Path gate rejected staged symlink entry ('{entry}').");
            }

            if (mode is "160000")
            {
                throw new SecurityException($"Path gate rejected staged submodule/gitlink entry ('{entry}').");
            }

            if (mode is not ("100644" or "100755"))
            {
                throw new SecurityException($"Path gate rejected unexpected git index mode '{mode}' ('{entry}').");
            }
        }
    }

    private static string ParseRealGitDirPath(string treePath, byte[] pointerBytes)
    {
        var text = Encoding.UTF8.GetString(pointerBytes).Trim();
        const string prefix = "gitdir:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Invalid .git pointer file format in '{treePath}'.");
        }

        var rawTarget = text[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            throw new SecurityException($"Empty gitdir target in '{treePath}/.git'.");
        }

        var combined = Path.IsPathRooted(rawTarget) ? rawTarget : Path.Combine(treePath, rawTarget);
        var fullGitDir = Path.GetFullPath(combined);
        if (!Directory.Exists(fullGitDir))
        {
            throw new SecurityException($"Resolved worktree gitdir '{fullGitDir}' does not exist.");
        }

        return fullGitDir;
    }

    private static IReadOnlyList<string> ParsePorcelainZPaths(string porcelainZOutput)
    {
        if (string.IsNullOrEmpty(porcelainZOutput))
        {
            return [];
        }

        var entries = porcelainZOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Length < 4 || entry[2] != ' ')
            {
                throw new SecurityException($"Unexpected git status entry format: '{entry}'.");
            }

            var xy = entry[..2];
            if (xy.Contains('U', StringComparison.Ordinal))
            {
                throw new SecurityException($"Unmerged conflict entry rejected: '{entry}'.");
            }

            paths.Add(entry[3..].Replace('\\', '/').Trim());
        }

        return paths;
    }

    private static bool TryParseLsRemoteSha(string lsRemoteOutput, string targetRef, out CommitSha sha)
    {
        sha = null!;
        foreach (var line in lsRemoteOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var refPart = line[(tab + 1)..].Trim();
            if (string.Equals(refPart, targetRef, StringComparison.Ordinal)
                && CommitSha.TryParse(line[..tab], out var parsed))
            {
                sha = parsed!;
                return true;
            }
        }

        return false;
    }

    private static string? TryExtractExistingPrUrl(string jsonOutput)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("url", out var urlProp)
                    && urlProp.ValueKind == JsonValueKind.String
                    && urlProp.GetString() is { Length: > 0 } url
                    && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static string? ParseGitHubOwnerRepo(string? remoteUrl)
        => GitHubRemoteParser.ParseOwnerRepo(remoteUrl);

    private static bool IsLocalFilesystemRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return false;
        }

        var u = remoteUrl.Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return u.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(u)
            || Directory.Exists(u);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
