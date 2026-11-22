using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

public sealed partial class WorkTreeService
{
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

        foreach (var candidate in new[] { "main", "master" })
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

    private async Task<(BranchName BaseBranch, string StartRef)> ResolveLocalBaseBranchAsync(
        string sourcePath,
        BranchName? requestedBranch,
        CancellationToken ct)
    {
        if (requestedBranch is not null)
        {
            var localRef = $"refs/heads/{requestedBranch.Value}";
            if (await RefExistsAsync(sourcePath, localRef, ct).ConfigureAwait(false))
            {
                return (requestedBranch, localRef);
            }

            throw new InvalidOperationException(
                $"Refusing to start from local base '{requestedBranch.Value}': branch '{requestedBranch.Value}' does not exist locally.");
        }

        var headBranch = await _git.RunSourceGitAsync(sourcePath, false, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (headBranch.ExitCode == 0 && headBranch.StdOut.Trim() is { Length: > 0 } b && b != "HEAD")
        {
            var localRef = $"refs/heads/{b}";
            if (await RefExistsAsync(sourcePath, localRef, ct).ConfigureAwait(false))
            {
                return (BranchName.Parse(b), localRef);
            }
        }

        foreach (var candidate in new[] { "main", "master" })
        {
            var localRef = $"refs/heads/{candidate}";
            if (await RefExistsAsync(sourcePath, localRef, ct).ConfigureAwait(false))
            {
                return (BranchName.Parse(candidate), localRef);
            }
        }

        throw new InvalidOperationException("Refusing to start: local repository has no local branches available as a base.");
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
