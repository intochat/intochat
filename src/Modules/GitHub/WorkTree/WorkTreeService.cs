using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Log;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Options controlling <see cref="WorkTreeService"/> home directory, repository allowlist path, and CLI timeouts.
/// </summary>
public sealed class WorkTreeOptions
{
    /// <summary>Override root home directory (defaults to <c>IntoChatOS__Home</c> or <c>%USERPROFILE%\.intochat</c>).</summary>
    public string? HomeDirectory { get; set; }

    /// <summary>Explicit path to <c>repos.json</c> (defaults to <c>&lt;HomeDirectory&gt;/repos.json</c>).</summary>
    public string? ReposConfigPath { get; set; }

    /// <summary>Maximum changed files permitted in a single Feature-to-PR run (default: 5).</summary>
    public int MaxChangedFiles { get; set; } = 5;

    /// <summary>Timeout per git/gh subprocess invocation.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Production implementation of <see cref="IWorkTree"/> enforcing:
/// <list type="bullet">
/// <item><description>Allowlisted repositories (<c>~/.intochat/repos.json</c>, <see cref="IConfiguration"/>, or programmatic registration)</description></item>
/// <item><description>Isolated external worktrees (<c>&lt;Home&gt;/worktrees/&lt;repo&gt;/&lt;runId&gt;</c>) that never touch the source checkout or its stashes</description></item>
/// <item><description>Triple Git Lock (R10): SHA-256 verification of <c>&lt;treePath&gt;/.git</c>, explicit <c>--git-dir</c> + <c>--work-tree</c>, <c>core.fsmonitor=false</c>, <c>core.hooksPath=/dev/null</c>, <c>protocol.file.allow=never</c>, and <c>GIT_CONFIG_NOSYSTEM=1</c></description></item>
/// <item><description>Deterministic C# path gate: NUL-delimited <c>git status -z --no-renames</c>, max 5 files, extension allowlist, dotfile/agent-instruction blocklist, symlink/submodule rejection</description></item>
/// <item><description>Single commit authored as <c>Ino &lt;ino@intochat.ai&gt;</c> + <c>gh pr create --draft</c> from an empty temp directory</description></item>
/// </list>
/// </summary>
public sealed partial class WorkTreeService : IWorkTree
{
    public const string AuthorName = "Ino";
    public const string AuthorEmail = "ino@intochat.ai";

    private static readonly FrozenSet<string> AllowedExtensions = new[]
    {
        ".cs",
        ".md",
        ".dart",
        ".txt",
        ".xaml",
        ".svg",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BlockedFileNames = new[]
    {
        "claude.md",
        "agents.md",
        "skill.md",
        "copilot-instructions.md",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, RegisteredRepository> _programmatic = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration? _configuration;
    private readonly WorkTreeOptions _options;
    private readonly ILogger<WorkTreeService> _logger;

    public WorkTreeService(
        IConfiguration? configuration = null,
        WorkTreeOptions? options = null,
        ILogger<WorkTreeService>? logger = null)
    {
        _configuration = configuration;
        _options = options ?? new WorkTreeOptions();
        _logger = logger ?? NullLogger<WorkTreeService>.Instance;
    }

    public string HomeDirectory => ResolveHomeDirectory();

    public void RegisterRepository(RegisteredRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.LocalPath);

        var normalized = repository with
        {
            Id = repository.Id.Trim(),
            LocalPath = Path.GetFullPath(repository.LocalPath.Trim()),
        };
        _programmatic[normalized.Id] = normalized;
    }

    public IReadOnlyList<RegisteredRepository> ListRepositories()
    {
        var merged = new Dictionary<string, RegisteredRepository>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in LoadFromConfiguration())
        {
            merged[entry.Id] = entry;
        }

        foreach (var entry in LoadFromReposJson())
        {
            merged[entry.Id] = entry;
        }

        foreach (var (key, entry) in _programmatic)
        {
            merged[key] = entry;
        }

        return [.. merged.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public RegisteredRepository ResolveRepository(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var wanted = repositoryId.Trim();
        var all = ListRepositories();

        var direct = all.FirstOrDefault(r => string.Equals(r.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        // Also match if caller passed "intochat/os" when registered under "os" with GitHubRepo == "intochat/os", or vice versa
        var bySlug = all.FirstOrDefault(r =>
            (!string.IsNullOrWhiteSpace(r.GitHubRepo) && string.Equals(r.GitHubRepo, wanted, StringComparison.OrdinalIgnoreCase))
            || (wanted.Contains('/', StringComparison.Ordinal) && string.Equals(r.Id, wanted[(wanted.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase)));
        if (bySlug is not null)
        {
            return bySlug;
        }

        var known = all.Count == 0 ? "none configured" : string.Join(", ", all.Select(r => r.Id));
        throw new UnauthorizedAccessException(
            $"Repository '{wanted}' is not registered in the allowlist ({ReposJsonPath}). Known repositories: {known}.");
    }

    public async Task<WorkTreeLease> PrepareAsync(
        string repositoryId,
        string runId,
        string goal,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var repo = ResolveRepository(repositoryId);
        var sourcePath = Path.GetFullPath(repo.LocalPath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Registered repository '{repo.Id}' path does not exist: {sourcePath}");
        }

        var safeRepoDir = SanitizeSegment(repo.Id);
        var safeRunId = SanitizeSegment(runId);
        var shortRunId = safeRunId.Length <= 8 ? safeRunId : safeRunId[..8];
        var slug = SlugifyGoal(goal);
        var branchName = $"ino/{slug}-{shortRunId}";

        var treePath = Path.GetFullPath(Path.Combine(HomeDirectory, "worktrees", safeRepoDir, safeRunId));
        if (Directory.Exists(treePath))
        {
            await RemoveWorktreeDirectoryAsync(sourcePath, treePath, ct).ConfigureAwait(false);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(treePath)!);

        // Determine origin URL (if any) without modifying the source checkout
        var originUrl = await TryGetOriginUrlAsync(sourcePath, ct).ConfigureAwait(false);
        var githubRepo = repo.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
        var allowLocalFileTransport = IsLocalFilesystemRemote(originUrl);

        // Fetch origin refs safely (updates refs/remotes/origin/* only; never touches working tree, index, HEAD, or stashes!)
        if (!string.IsNullOrWhiteSpace(originUrl))
        {
            var fetch = await RunProcessAsync(
                "git",
                BuildSourceGitArgs(sourcePath, allowLocalFileTransport, "fetch", "origin", "--prune", "--no-tags"),
                workingDirectory: sourcePath,
                ct: ct).ConfigureAwait(false);
            if (fetch.ExitCode != 0)
            {
                _logger.LogWarning("Non-fatal git fetch warning on {Repo}: {StdErr}", repo.Id, fetch.StdErr);
            }
        }

        var (resolvedBaseBranch, startRef) = await ResolveBaseBranchAndStartRefAsync(
            sourcePath,
            baseBranch ?? repo.DefaultBaseBranch,
            hasOrigin: !string.IsNullOrWhiteSpace(originUrl),
            ct).ConfigureAwait(false);

        var baseShaResult = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "rev-parse", "--verify", $"{startRef}^{{commit}}"),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);
        EnsureSuccess(baseShaResult, $"resolve base commit '{startRef}'");
        var baseSha = baseShaResult.StdOut.Trim();

        // Prune stale worktree metadata if an earlier crashed run left a dead registration
        _ = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "worktree", "prune"),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);

        // Create external worktree on new branch ino/<slug>-<shortRunId> starting strictly at baseSha
        var addResult = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "worktree", "add", "--no-track", "-B", branchName, treePath, baseSha),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);
        EnsureSuccess(addResult, $"create isolated worktree at '{treePath}'");

        // Record Triple Git Lock (R10): exact .git pointer file bytes + canonical RealGitDirPath
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
            BaseBranch: resolvedBaseBranch,
            BaseCommitSha: baseSha,
            BranchName: branchName,
            GitHubRepo: githubRepo);
    }

    public async Task<WorkTreeCommit> VerifyAndCommitAsync(
        WorkTreeLease lease,
        string commitMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);

        // 1. Triple Git Lock (R10): Verify <TreePath>/.git is still a regular non-reparse file with 100% identical bytes!
        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        // 2. Verify HEAD is still at BaseCommitSha (unless idempotently re-entering on an already-created 1-commit branch)
        var headCheck = await RunLockedGitAsync(lease, allowLocalFileTransport: false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        EnsureSuccess(headCheck, "read worktree HEAD");
        var currentHead = headCheck.StdOut.Trim();

        // 3. Deterministic C# Path Gate via NUL-delimited `git status -z --porcelain=v1 --no-renames -uall`
        var statusResult = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: false,
            ct,
            "status", "-z", "--porcelain=v1", "--no-renames", "-uall").ConfigureAwait(false);
        EnsureSuccess(statusResult, "inspect worktree status");

        var changedPaths = ParsePorcelainZPaths(statusResult.StdOut);

        // Idempotent recovery: if step already committed 1 commit ahead of BaseCommitSha and working tree is now clean, return that commit!
        if (changedPaths.Count == 0 && !string.Equals(currentHead, lease.BaseCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSingleCommitAheadAsync(lease, currentHead, ct).ConfigureAwait(false);
            var existingFilesResult = await RunLockedGitAsync(
                lease,
                allowLocalFileTransport: false,
                ct,
                "diff", "--name-only", "-z", "--no-renames", $"{lease.BaseCommitSha}..{currentHead}").ConfigureAwait(false);
            EnsureSuccess(existingFilesResult, "list committed files");
            var committedFiles = existingFilesResult.StdOut
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeRepoRelativePath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
            ValidateChangedPaths(lease.TreePath, committedFiles);
            var existingDiff = await RenderUnifiedDiffAsync(lease, currentHead, ct).ConfigureAwait(false);
            return BuildCommitRecord(currentHead, committedFiles, existingDiff);
        }

        if (!string.Equals(currentHead, lease.BaseCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Worktree HEAD ('{currentHead}') diverged from base commit ('{lease.BaseCommitSha}') before host commit.");
        }

        if (changedPaths.Count == 0)
        {
            throw new InvalidOperationException("Coder produced zero file modifications in the worktree.");
        }

        ValidateChangedPaths(lease.TreePath, changedPaths);

        // 4. Stage validated paths explicitly and verify index modes (no 120000 symlinks or 160000 gitlinks)
        var addArgs = new List<string>(changedPaths.Count + 3) { "add", "-A", "--" };
        addArgs.AddRange(changedPaths);
        var stageResult = await RunLockedGitAsync(lease, allowLocalFileTransport: false, ct, [.. addArgs]).ConfigureAwait(false);
        EnsureSuccess(stageResult, "stage validated files");

        var lsStage = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: false,
            ct,
            ["ls-files", "--stage", "-z", "--", .. changedPaths]).ConfigureAwait(false);
        EnsureSuccess(lsStage, "verify staged git index modes");
        VerifyIndexModes(lsStage.StdOut);

        // 5. Create exactly ONE commit authored as Ino <ino@intochat.ai> with GPG signing & hooks disabled
        var cleanMessage = SanitizeCommitMessage(commitMessage);
        var commitResult = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: false,
            ct,
            "-c", $"user.name={AuthorName}",
            "-c", $"user.email={AuthorEmail}",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", cleanMessage).ConfigureAwait(false);
        EnsureSuccess(commitResult, "create single worktree commit");

        // Re-verify Triple Git Lock after commit
        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var shaResult = await RunLockedGitAsync(lease, allowLocalFileTransport: false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        EnsureSuccess(shaResult, "read committed SHA");
        var commitSha = shaResult.StdOut.Trim();

        await EnsureSingleCommitAheadAsync(lease, commitSha, ct).ConfigureAwait(false);

        var diff = await RenderUnifiedDiffAsync(lease, commitSha, ct).ConfigureAwait(false);
        return BuildCommitRecord(commitSha, changedPaths, diff);
    }

    public async Task<PublishedPrResult> PublishAsync(
        WorkTreeLease lease,
        string approvedSha,
        string prTitle,
        string prBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(prTitle);

        if (!lease.BranchName.StartsWith("ino/", StringComparison.Ordinal))
        {
            throw new SecurityException($"Refusing to push branch '{lease.BranchName}': branch name must start with 'ino/'.");
        }

        // 1. Re-verify Triple Git Lock + exact HEAD SHA + 1-commit-ahead + clean working tree
        await VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);

        var headResult = await RunLockedGitAsync(lease, allowLocalFileTransport: false, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        EnsureSuccess(headResult, "verify HEAD before push");
        var headSha = headResult.StdOut.Trim();
        if (!string.Equals(headSha, approvedSha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Approved commit SHA mismatch: person approved '{approvedSha}', but worktree HEAD is '{headSha}'.");
        }

        await EnsureSingleCommitAheadAsync(lease, headSha, ct).ConfigureAwait(false);

        var dirtyCheck = await RunLockedGitAsync(lease, allowLocalFileTransport: false, ct, "status", "--porcelain=v1", "-z", "-uall").ConfigureAwait(false);
        EnsureSuccess(dirtyCheck, "verify clean worktree before push");
        if (!string.IsNullOrEmpty(dirtyCheck.StdOut))
        {
            throw new SecurityException("Refusing to push: worktree contains uncommitted modifications after commit verification.");
        }

        // 2. Push exact <approvedSha>:refs/heads/<branchName> to origin
        var originUrl = await TryGetOriginUrlAsync(lease.SourceRepoPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            throw new InvalidOperationException($"Repository '{lease.RepositoryId}' has no 'origin' remote configured.");
        }

        var allowLocalFileTransport = IsLocalFilesystemRemote(originUrl);
        var targetRef = $"refs/heads/{lease.BranchName}";
        var pushResult = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: allowLocalFileTransport,
            ct,
            "push", "--porcelain", "--no-verify", "origin", $"{headSha}:{targetRef}").ConfigureAwait(false);
        EnsureSuccess(pushResult, $"push '{headSha}:{targetRef}' to origin");

        // 3. Create Draft Pull Request via `gh pr create --draft` from an EMPTY temp directory when remote is GitHub
        string? prUrl = null;
        var githubRepo = lease.GitHubRepo ?? ParseGitHubOwnerRepo(originUrl);
        if (!string.IsNullOrWhiteSpace(githubRepo) && !allowLocalFileTransport)
        {
            prUrl = await CreateGitHubDraftPrFromEmptyDirAsync(
                githubRepo,
                lease.BranchName,
                lease.BaseBranch,
                prTitle.Trim(),
                prBody ?? string.Empty,
                ct).ConfigureAwait(false);
        }

        // 4. Clean up isolated worktree without touching source checkout
        await CleanupAsync(lease, ct).ConfigureAwait(false);

        return new PublishedPrResult(
            CommitSha: headSha,
            BranchName: lease.BranchName,
            PushedRef: targetRef,
            PullRequestUrl: prUrl);
    }

    public async Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await RemoveWorktreeDirectoryAsync(lease.SourceRepoPath, lease.TreePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies Triple Git Lock (R10): <c>&lt;TreePath&gt;/.git</c> exists as a regular file (never a directory or symlink/junction),
    /// its exact bytes match <see cref="WorkTreeLease.GitPointerSha256Hex"/> in constant time, and its <c>gitdir:</c> still resolves
    /// to <see cref="WorkTreeLease.RealGitDirPath"/>.
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

    internal void ValidateChangedPaths(string treeRoot, IReadOnlyList<string> paths)
    {
        if (paths.Count > _options.MaxChangedFiles)
        {
            throw new SecurityException(
                $"Path gate rejected change: {paths.Count} files modified, exceeding the maximum of {_options.MaxChangedFiles} files.");
        }

        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(treeRoot));
        foreach (var rawPath in paths)
        {
            var rel = NormalizeRepoRelativePath(rawPath);
            if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel))
            {
                throw new SecurityException($"Path gate rejected invalid or rooted path '{rawPath}'.");
            }

            var segments = rel.Split('/', StringSplitOptions.None);
            foreach (var seg in segments)
            {
                if (seg.Length == 0 || seg is "." or "..")
                {
                    throw new SecurityException($"Path gate rejected traversal segment in '{rel}'.");
                }

                // Block ALL dotfiles and dot-directories (.git, .github, .claude, .vscode, .gitignore, etc.)
                if (seg.StartsWith('.'))
                {
                    throw new SecurityException($"Path gate rejected dotfile or hidden directory path '{rel}'.");
                }
            }

            var fileName = segments[^1];
            if (BlockedFileNames.Contains(fileName))
            {
                throw new SecurityException($"Path gate rejected agent instruction file '{rel}'.");
            }

            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            {
                throw new SecurityException(
                    $"Path gate rejected disallowed file extension '{ext}' on '{rel}'. Allowed: {string.Join(", ", AllowedExtensions.Order())}.");
            }

            // Canonicalize on disk and verify no symlink / junction along any ancestor segment or leaf
            var candidateFull = Path.GetFullPath(Path.Combine(treeRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidateFull.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new SecurityException($"Path gate rejected path escaping worktree root: '{rel}'.");
            }

            EnsureNoReparsePointsInHierarchy(treeRoot, candidateFull);
        }
    }

    private static void EnsureNoReparsePointsInHierarchy(string treeRoot, string targetFullPath)
    {
        var stopAt = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = targetFullPath;
        while (!string.IsNullOrEmpty(current) && !PathsEqual(current, stopAt))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var info = new FileInfo(current);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    throw new SecurityException($"Path gate rejected symlink or reparse point at '{current}'.");
                }
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void VerifyIndexModes(string lsFilesStageZ)
    {
        foreach (var entry in lsFilesStageZ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "<mode> <object> <stage>\t<file>"
            var space = entry.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0) { continue; }
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

    private async Task EnsureSingleCommitAheadAsync(WorkTreeLease lease, string commitSha, CancellationToken ct)
    {
        var countResult = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: false,
            ct,
            "rev-list", "--count", $"{lease.BaseCommitSha}..{commitSha}").ConfigureAwait(false);
        EnsureSuccess(countResult, "verify commit count ahead of base");
        if (!int.TryParse(countResult.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != 1)
        {
            throw new SecurityException(
                $"Expected exactly 1 commit ahead of base '{lease.BaseCommitSha}', but found {countResult.StdOut.Trim()}.");
        }
    }

    private async Task<string> RenderUnifiedDiffAsync(WorkTreeLease lease, string commitSha, CancellationToken ct)
    {
        var diffResult = await RunLockedGitAsync(
            lease,
            allowLocalFileTransport: false,
            ct,
            "diff", "--no-color", "--no-ext-diff", "--no-renames", "--unified=3", $"{lease.BaseCommitSha}..{commitSha}").ConfigureAwait(false);
        EnsureSuccess(diffResult, "render host unified diff");
        return diffResult.StdOut;
    }

    private static WorkTreeCommit BuildCommitRecord(string commitSha, IReadOnlyList<string> changedPaths, string diff)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.StartsWith('+')) { additions++; }
            else if (line.StartsWith('-')) { deletions++; }
        }

        return new WorkTreeCommit(
            CommitSha: commitSha,
            ChangedFiles: [.. changedPaths.Select(NormalizeRepoRelativePath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)],
            HostRenderedDiff: diff,
            Additions: additions,
            Deletions: deletions);
    }

    private async Task<string> CreateGitHubDraftPrFromEmptyDirAsync(
        string githubRepo,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct)
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "intochat-gh-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var result = await RunProcessAsync(
                "gh",
                [
                    "pr", "create",
                    "--draft",
                    "--repo", githubRepo,
                    "--head", headBranch,
                    "--base", baseBranch,
                    "--title", title,
                    "--body", body,
                ],
                workingDirectory: emptyDir,
                ct: ct).ConfigureAwait(false);
            EnsureSuccess(result, $"create draft PR on '{githubRepo}'");
            var url = result.StdOut
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ?? result.StdOut.Trim();
            return url;
        }
        finally
        {
            try { Directory.Delete(emptyDir, recursive: true); }
            catch (Exception ex) { _logger.LogSwallowed(ex, "Best-effort cleanup of empty gh working directory."); }
        }
    }

    private async Task RemoveWorktreeDirectoryAsync(string sourceRepoPath, string treePath, CancellationToken ct)
    {
        if (Directory.Exists(sourceRepoPath))
        {
            _ = await RunProcessAsync(
                "git",
                BuildSourceGitArgs(sourceRepoPath, allowLocalFileTransport: false, "worktree", "remove", "--force", treePath),
                workingDirectory: sourceRepoPath,
                ct: ct).ConfigureAwait(false);
            _ = await RunProcessAsync(
                "git",
                BuildSourceGitArgs(sourceRepoPath, allowLocalFileTransport: false, "worktree", "prune"),
                workingDirectory: sourceRepoPath,
                ct: ct).ConfigureAwait(false);
        }

        if (Directory.Exists(treePath))
        {
            try
            {
                ClearReadOnlyAttributes(treePath);
                Directory.Delete(treePath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogSwallowed(ex, "Best-effort directory delete during worktree cleanup.");
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
            catch (IOException ex) { _logger.LogSwallowed(ex, "Best-effort clear ReadOnly attribute (IO)."); }
            catch (UnauthorizedAccessException ex) { _logger.LogSwallowed(ex, "Best-effort clear ReadOnly attribute (ACL)."); }
        }
    }

    private async Task<string?> TryGetOriginUrlAsync(string sourcePath, CancellationToken ct)
    {
        var res = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "remote", "get-url", "origin"),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);
        return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut) ? res.StdOut.Trim() : null;
    }

    private async Task<(string BaseBranch, string StartRef)> ResolveBaseBranchAndStartRefAsync(
        string sourcePath,
        string? requestedBranch,
        bool hasOrigin,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedBranch))
        {
            var clean = requestedBranch.Trim();
            if (clean.StartsWith("origin/", StringComparison.Ordinal))
            {
                clean = clean["origin/".Length..];
            }

            if (hasOrigin && await RefExistsAsync(sourcePath, $"refs/remotes/origin/{clean}", ct).ConfigureAwait(false))
            {
                return (clean, $"refs/remotes/origin/{clean}");
            }
            if (await RefExistsAsync(sourcePath, $"refs/heads/{clean}", ct).ConfigureAwait(false))
            {
                return (clean, $"refs/heads/{clean}");
            }
        }

        // 1. Try git symbolic-ref refs/remotes/origin/HEAD (e.g. refs/remotes/origin/refactor/ddd-clean-os)
        if (hasOrigin)
        {
            var sym = await RunProcessAsync(
                "git",
                BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "symbolic-ref", "-q", "refs/remotes/origin/HEAD"),
                workingDirectory: sourcePath,
                ct: ct).ConfigureAwait(false);
            if (sym.ExitCode == 0 && sym.StdOut.Trim() is { Length: > 0 } symRef)
            {
                const string prefix = "refs/remotes/origin/";
                if (symRef.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var branch = symRef[prefix.Length..];
                    if (await RefExistsAsync(sourcePath, symRef, ct).ConfigureAwait(false))
                    {
                        return (branch, symRef);
                    }
                }
            }

            // 2. Probe well-known remote branches
            foreach (var candidate in new[] { "refactor/ddd-clean-os", "main", "master" })
            {
                var remoteRef = $"refs/remotes/origin/{candidate}";
                if (await RefExistsAsync(sourcePath, remoteRef, ct).ConfigureAwait(false))
                {
                    return (candidate, remoteRef);
                }
            }
        }

        // 3. Fallback to current source branch or local main/master/HEAD (e.g. local repo without remote refs)
        var headBranch = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "rev-parse", "--abbrev-ref", "HEAD"),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);
        if (headBranch.ExitCode == 0 && headBranch.StdOut.Trim() is { Length: > 0 } b && b != "HEAD")
        {
            if (hasOrigin && await RefExistsAsync(sourcePath, $"refs/remotes/origin/{b}", ct).ConfigureAwait(false))
            {
                return (b, $"refs/remotes/origin/{b}");
            }
            return (b, "HEAD");
        }

        return ("main", "HEAD");
    }

    private async Task<bool> RefExistsAsync(string sourcePath, string fullRef, CancellationToken ct)
    {
        var res = await RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport: false, "show-ref", "--verify", "--quiet", fullRef),
            workingDirectory: sourcePath,
            ct: ct).ConfigureAwait(false);
        return res.ExitCode == 0;
    }

    private Task<ProcessOutput> RunLockedGitAsync(
        WorkTreeLease lease,
        bool allowLocalFileTransport,
        CancellationToken ct,
        params string[] gitSubArgs)
    {
        var protocolPolicy = allowLocalFileTransport ? "protocol.file.allow=always" : "protocol.file.allow=never";
        var args = new List<string>(gitSubArgs.Length + 8)
        {
            $"--git-dir={lease.RealGitDirPath}",
            $"--work-tree={lease.TreePath}",
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null",
            "-c", protocolPolicy,
        };
        args.AddRange(gitSubArgs);
        return RunProcessAsync("git", args, workingDirectory: lease.TreePath, ct: ct);
    }

    private static List<string> BuildSourceGitArgs(string sourcePath, bool allowLocalFileTransport, params string[] gitSubArgs)
    {
        var protocolPolicy = allowLocalFileTransport ? "protocol.file.allow=always" : "protocol.file.allow=never";
        var args = new List<string>(gitSubArgs.Length + 8)
        {
            "-C", sourcePath,
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null",
            "-c", protocolPolicy,
        };
        args.AddRange(gitSubArgs);
        return args;
    }

    private async Task<ProcessOutput> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Harden git subprocess environment: prevent interactive prompts and system config hijacks
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.CommandTimeout);

        var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);
            return new ProcessOutput(process.ExitCode, stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (Exception ex) { _logger.LogSwallowed(ex, "Best-effort kill on timed-out/cancelled git/gh process."); }
            throw;
        }
    }

    private static void EnsureSuccess(ProcessOutput output, string action)
    {
        if (output.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(output.StdErr) ? output.StdOut.Trim() : output.StdErr.Trim();
            throw new InvalidOperationException($"Failed to {action} (exit {output.ExitCode}): {detail}");
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

        var combined = Path.IsPathRooted(rawTarget)
            ? rawTarget
            : Path.Combine(treePath, rawTarget);
        var fullGitDir = Path.GetFullPath(combined);
        if (!Directory.Exists(fullGitDir))
        {
            throw new SecurityException($"Resolved worktree gitdir '{fullGitDir}' does not exist.");
        }
        return fullGitDir;
    }

    internal static IReadOnlyList<string> ParsePorcelainZPaths(string porcelainZOutput)
    {
        if (string.IsNullOrEmpty(porcelainZOutput))
        {
            return [];
        }

        var entries = porcelainZOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            // Porcelain v1 -z --no-renames format: "XY PATH" (3 chars prefix: X, Y, space)
            if (entry.Length < 4 || entry[2] != ' ')
            {
                throw new SecurityException($"Unexpected git status entry format: '{entry}'.");
            }

            var xy = entry[..2];
            if (xy.Contains('U', StringComparison.Ordinal))
            {
                throw new SecurityException($"Unmerged conflict entry rejected: '{entry}'.");
            }

            paths.Add(NormalizeRepoRelativePath(entry[3..]));
        }

        return paths;
    }

    internal static string? ParseGitHubOwnerRepo(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        var match = GitHubUrlRegex().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }
        return $"{owner}/{repo}";
    }

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

    private string ResolveHomeDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.HomeDirectory))
        {
            return Path.GetFullPath(_options.HomeDirectory);
        }

        var fromConfig = _configuration?["IntoChatOS:Home"] ?? _configuration?["Home"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return Path.GetFullPath(fromConfig);
        }

        var fromEnv = Environment.GetEnvironmentVariable("IntoChatOS__Home");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(profile, ".intochat"));
    }

    private string ReposJsonPath => !string.IsNullOrWhiteSpace(_options.ReposConfigPath)
        ? Path.GetFullPath(_options.ReposConfigPath)
        : Path.Combine(HomeDirectory, "repos.json");

    private IEnumerable<RegisteredRepository> LoadFromConfiguration()
    {
        if (_configuration is null)
        {
            yield break;
        }

        foreach (var sectionName in new[] { "IntoChatOS:Repositories", "GitHub:Repositories", "Repositories" })
        {
            var section = _configuration.GetSection(sectionName);
            foreach (var child in section.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    yield return new RegisteredRepository(child.Key, Path.GetFullPath(child.Value));
                }
                else if (child["path"] is { Length: > 0 } p || child["localPath"] is { Length: > 0 } lp)
                {
                    var local = child["path"] ?? child["localPath"]!;
                    yield return new RegisteredRepository(
                        child["id"] ?? child.Key,
                        Path.GetFullPath(local),
                        child["githubRepo"] ?? child["remote"],
                        child["baseBranch"] ?? child["defaultBaseBranch"]);
                }
            }
        }
    }

    private IEnumerable<RegisteredRepository> LoadFromReposJson()
    {
        var path = ReposJsonPath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ParseReposJsonRoot(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse repository allowlist at {Path}", path);
            return [];
        }
    }

    internal static IReadOnlyList<RegisteredRepository> ParseReposJsonRoot(JsonElement root)
    {
        var list = new List<RegisteredRepository>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryParseRepoObject(null, item) is { } repo)
                {
                    list.Add(repo);
                }
            }
            return list;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("repositories", out var reposProp))
            {
                return ParseReposJsonRoot(reposProp);
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } strPath)
                {
                    list.Add(new RegisteredRepository(prop.Name, Path.GetFullPath(strPath)));
                }
                else if (TryParseRepoObject(prop.Name, prop.Value) is { } repo)
                {
                    list.Add(repo);
                }
            }
        }

        return list;
    }

    private static RegisteredRepository? TryParseRepoObject(string? fallbackId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetStringIgnoreCase(element, "id") ?? GetStringIgnoreCase(element, "name") ?? fallbackId;
        var localPath = GetStringIgnoreCase(element, "path") ?? GetStringIgnoreCase(element, "localPath");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        var githubRepo = GetStringIgnoreCase(element, "githubRepo") ?? GetStringIgnoreCase(element, "repo") ?? GetStringIgnoreCase(element, "slug");
        var baseBranch = GetStringIgnoreCase(element, "baseBranch") ?? GetStringIgnoreCase(element, "defaultBaseBranch");
        return new RegisteredRepository(id.Trim(), Path.GetFullPath(localPath.Trim()), githubRepo?.Trim(), baseBranch?.Trim());
    }

    private static string? GetStringIgnoreCase(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }

    private static string NormalizeRepoRelativePath(string path) =>
        path.Replace('\\', '/').Trim();

    private static string EnsureTrailingSeparator(string fullPath) =>
        fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SanitizeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '/' or '.' or ' ')
            {
                sb.Append('-');
            }
        }

        var res = CollapseHyphensRegex().Replace(sb.ToString(), "-").Trim('-');
        return res.Length == 0 ? "run" : (res.Length <= 40 ? res : res[..40].TrimEnd('-'));
    }

    internal static string SlugifyGoal(string goal)
    {
        var sb = new StringBuilder();
        foreach (var ch in goal)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append('-');
            }
        }

        var slug = CollapseHyphensRegex().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length == 0) { return "change"; }
        return slug.Length <= 28 ? slug : slug[..28].TrimEnd('-');
    }

    private static string SanitizeCommitMessage(string message)
    {
        var clean = message.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return clean.Length == 0 ? "chore(ino): automated change" : clean;
    }

    private sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

    [GeneratedRegex(@"(?:github\.com[:/])(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex CollapseHyphensRegex();
}
