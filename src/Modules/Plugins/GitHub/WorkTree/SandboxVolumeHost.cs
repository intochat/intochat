using System.Collections.Concurrent;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

internal sealed class SandboxVolumeHost(WorkTreeOptions options, ILogger logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public string GetVolumeRoot(string homeDirectory)
        => !string.IsNullOrWhiteSpace(options.VolumeMountRoot)
            ? Path.GetFullPath(options.VolumeMountRoot)
            : Path.GetFullPath(Path.Combine(homeDirectory, "volumes"));

    public string GetLeaseRoot(string homeDirectory, string leaseId)
        => Path.GetFullPath(Path.Combine(GetVolumeRoot(homeDirectory), "leases", BranchName.SanitizeSegment(leaseId)));

    public SemaphoreSlim GateFor(string repoPath)
        => _repoGates.GetOrAdd(
            Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            static _ => new SemaphoreSlim(1, 1));

    public async Task<string> EnsureMirrorAsync(
        RegisteredRepository repo,
        string homeDirectory,
        GitCommandRunner git,
        CancellationToken ct)
    {
        var safeRepo = BranchName.SanitizeSegment(repo.Id);
        var mirrorPath = Path.GetFullPath(Path.Combine(GetVolumeRoot(homeDirectory), "mirrors", safeRepo + ".git"));
        var gate = GateFor(mirrorPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (fetchUrl, pushUrls) = await ResolveUpstreamUrlsAsync(repo, git, ct).ConfigureAwait(false);
            Directory.CreateDirectory(mirrorPath);

            if (!File.Exists(Path.Combine(mirrorPath, "HEAD")))
            {
                var init = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, "init", "--bare", "--initial-branch=main").ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(init, $"initialize bare sandbox mirror for '{repo.Id}'");

                var addOrigin = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, "remote", "add", "origin", fetchUrl).ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(addOrigin, $"configure origin remote on bare sandbox mirror for '{repo.Id}'");

                var setRefspec = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*").ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(setRefspec, $"configure origin fetch refspec on bare sandbox mirror for '{repo.Id}'");
            }
            else
            {
                var setUrl = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, "remote", "set-url", "origin", fetchUrl).ConfigureAwait(false);
                GitCommandRunner.EnsureSuccess(setUrl, $"sync origin fetch url on bare sandbox mirror for '{repo.Id}'");
            }

            // Sync push URLs so origin changes or multi-push remotes match source repository semantics.
            _ = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, "config", "--unset-all", "remote.origin.pushurl").ConfigureAwait(false);
            if (pushUrls.Length > 0)
            {
                for (var i = 0; i < pushUrls.Length; i++)
                {
                    string[] pushArgs = i == 0
                        ? ["remote", "set-url", "--push", "origin", pushUrls[i]]
                        : ["remote", "set-url", "--add", "--push", "origin", pushUrls[i]];
                    var setPush = await git.RunSourceGitAsync(mirrorPath, allowLocalFileTransport: true, ct, pushArgs).ConfigureAwait(false);
                    GitCommandRunner.EnsureSuccess(setPush, $"sync origin push url on bare sandbox mirror for '{repo.Id}'");
                }
            }

            return mirrorPath;
        }
        finally
        {
            gate.Release();
        }
    }

    public void CleanupLeaseVolumeDirectory(WorkTreeLease lease)
    {
        if (lease.Backend != WorkTreeBackendKind.ContainerVolume)
        {
            return;
        }

        var leaseDir = Path.GetDirectoryName(Path.GetFullPath(lease.TreePath));
        if (string.IsNullOrWhiteSpace(leaseDir) || !Directory.Exists(leaseDir))
        {
            return;
        }

        try
        {
            Directory.Delete(leaseDir, recursive: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogSwallowed(ex, "Best-effort delete of empty container volume lease directory.");
        }
    }

    private static async Task<(string FetchUrl, string[] PushUrls)> ResolveUpstreamUrlsAsync(
        RegisteredRepository repo,
        GitCommandRunner git,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(repo.RemoteCloneUrl))
        {
            var url = repo.RemoteCloneUrl.Trim();
            return (url, [url]);
        }

        var localPath = Path.GetFullPath(repo.LocalPath);
        if (!Directory.Exists(localPath))
        {
            throw new DirectoryNotFoundException(
                $"Registered repository '{repo.Id}' has no RemoteCloneUrl and local path does not exist: {localPath}");
        }

        var fetchRes = await git.RunSourceGitAsync(localPath, allowLocalFileTransport: false, ct, "remote", "get-url", "origin").ConfigureAwait(false);
        if (fetchRes.ExitCode != 0 || string.IsNullOrWhiteSpace(fetchRes.StdOut))
        {
            throw new InvalidOperationException($"Repository '{repo.Id}' has no 'origin' remote configured.");
        }

        var pushRes = await git.RunSourceGitAsync(localPath, allowLocalFileTransport: false, ct, "remote", "get-url", "--push", "--all", "origin").ConfigureAwait(false);
        var pushUrls = pushRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(pushRes.StdOut)
            ? pushRes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [fetchRes.StdOut.Trim()];

        return (fetchRes.StdOut.Trim(), pushUrls);
    }
}
