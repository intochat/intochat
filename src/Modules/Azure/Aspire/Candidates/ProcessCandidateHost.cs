using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

internal sealed class CandidateProcessHandle : IDisposable
{
    private const int MaxCapturedChars = 64 * 1024;
    private readonly Process? _process;
    private readonly Lock _outputGate = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Func<bool>? _hasExited;
    private readonly Func<int>? _exitCode;
    private readonly Func<string>? _stdoutReader;
    private readonly Func<string>? _stderrReader;
    private readonly Action<bool>? _kill;
    private readonly Func<CancellationToken, Task>? _waitForExit;
    private readonly int _fakeId;
    private bool _disposed;

    private CandidateProcessHandle(Process process)
    {
        _process = process;
        _process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
        _process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);
        if (!_process.Start())
        {
            throw new CandidateHostException("run", $"Failed to start candidate process '{_process.StartInfo.FileName}'.");
        }

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
        }

        if (_process.StartInfo.RedirectStandardError)
        {
            _process.BeginErrorReadLine();
        }
    }

    internal CandidateProcessHandle(
        int pid,
        Func<bool> hasExited,
        Func<int> exitCode,
        Func<string> stdout,
        Func<string> stderr,
        Action<bool> kill,
        Func<CancellationToken, Task> waitForExit)
    {
        _fakeId = pid;
        _hasExited = hasExited;
        _exitCode = exitCode;
        _stdoutReader = stdout;
        _stderrReader = stderr;
        _kill = kill;
        _waitForExit = waitForExit;
    }

    public static CandidateProcessHandle Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        return new CandidateProcessHandle(process);
    }

    public int Id => _process is not null ? _process.Id : _fakeId;
    public bool HasExited => _process is not null ? _process.HasExited : _hasExited!();
    public int ExitCode => _process is not null ? _process.ExitCode : _exitCode!();

    public string StandardOutputSnapshot
    {
        get
        {
            if (_process is null)
            {
                return _stdoutReader!();
            }

            lock (_outputGate)
            {
                return _stdout.ToString();
            }
        }
    }

    public string StandardErrorSnapshot
    {
        get
        {
            if (_process is null)
            {
                return _stderrReader!();
            }

            lock (_outputGate)
            {
                return _stderr.ToString();
            }
        }
    }

    public void Kill(bool entireProcessTree)
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree);
            }

            return;
        }

        _kill!(entireProcessTree);
    }

    public Task WaitForExitAsync(CancellationToken ct)
        => _process is not null ? _process.WaitForExitAsync(ct) : _waitForExit!(ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process?.Dispose();
    }

    private void Append(StringBuilder buffer, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputGate)
        {
            if (buffer.Length > MaxCapturedChars)
            {
                buffer.Remove(0, buffer.Length - (MaxCapturedChars / 2));
            }

            buffer.AppendLine(line);
        }
    }
}

// Runs each candidate as an isolated headless local OS process on a dynamically allocated
// loopback TCP port (never 5080/5081) with an isolated temp home (never ~/.intochat).
internal sealed partial class ProcessCandidateHost : ICandidateHost
{
    private const string OsProject = "src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj";
    private const int LiveHttpPort = 5080;
    private const int LiveMcpPort = 5081;
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _processes;
    private readonly Func<CancellationToken, Task<string>> _workdir;
    private readonly Func<CancellationToken, Task<CandidateSecrets>> _secrets;
    private readonly Func<ProcessStartInfo, CandidateProcessHandle> _launcher;
    private readonly Func<int> _portAllocator;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, TrackedProcess> _tracked = new(StringComparer.Ordinal);

    internal sealed record TrackedProcess(
        string Sha,
        CandidateProcessHandle Process,
        int HttpPort,
        string HomeDirectory,
        string WorktreeDirectory);

    public ProcessCandidateHost(IModuleIntegrations<AspireModule> integrations, IProcessRunner processes)
        : this(
            processes,
            ct => Workdir(integrations, ct),
            ct => Secrets(integrations, ct),
            launcher: null,
            http: new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            portAllocator: null)
    {
    }

    internal ProcessCandidateHost(
        IProcessRunner processes,
        Func<CancellationToken, Task<string>> workdir,
        Func<CancellationToken, Task<CandidateSecrets>>? secrets = null,
        Func<ProcessStartInfo, CandidateProcessHandle>? launcher = null,
        HttpClient? http = null,
        Func<int>? portAllocator = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _workdir = workdir ?? throw new ArgumentNullException(nameof(workdir));
        _secrets = secrets ?? (_ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())));
        _launcher = launcher ?? CandidateProcessHandle.Start;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _portAllocator = portAllocator ?? AllocateEphemeralLoopbackPort;
    }

    public async Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = await _workdir(ct).ConfigureAwait(false);
        var git = Git(root);
        var staging = Path.Combine(root, "worktrees", $"staging-{Guid.NewGuid():N}");
        var added = false;
        try
        {
            await git.AddWorktree(staging, baseSha, ct).ConfigureAwait(false);
            added = true;
            foreach (var (path, content) in files)
            {
                var target = Path.GetFullPath(Path.Combine(staging, path));
                if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CandidateHostException("prepare", $"{path} leaves the worktree");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, content, ct).ConfigureAwait(false);
            }

            await git.Stage(staging, ct).ConfigureAwait(false);
            await git.Commit(staging, summary, ct).ConfigureAwait(false);
            var sha = await git.ShortSha(staging, ct).ConfigureAwait(false);
            var worktree = WorktreeOf(root, sha);
            await git.MoveWorktree(staging, worktree, ct).ConfigureAwait(false);
            return new PreparedCandidate(sha, worktree);
        }
        catch (Exception error)
        {
            if (added)
            {
                await git.RemoveWorktree(staging, CancellationToken.None).ConfigureAwait(false);
            }

            if (error is CandidateHostException or OperationCanceledException)
            {
                throw;
            }

            throw new CandidateHostException("prepare", error.Message);
        }
    }

    public async Task BuildGate(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        await Dotnet("gate", worktree, ct, "build", OsProject, "-c", "Release", "--nologo").ConfigureAwait(false);
    }

    public async Task<string> Publish(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        var build = Path.Combine(root, "builds", sha);
        var output = Path.Combine(build, "os");
        await Dotnet("publish", worktree, ct, "publish", OsProject, "-c", "Release", "-o", output, "--nologo").ConfigureAwait(false);
        return build;
    }

    public async Task<RunningCandidate> Run(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var given = await _secrets(ct).ConfigureAwait(false);
        var httpPort = AllocateSafeLoopbackPort();
        var candidateHome = Path.GetFullPath(Path.Combine(root, "homes", sha));
        EnsureNotLiveHome(candidateHome);
        Directory.CreateDirectory(candidateHome);

        var outputDir = Path.Combine(root, "builds", sha, "os");
        var kernelDll = Path.Combine(outputDir, "IntoChat.OS.Kernel.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Directory.Exists(outputDir) ? outputDir : candidateHome,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(kernelDll);

        ChildEnvironmentPolicy.Scrub(startInfo);
        foreach (var (key, value) in given.Model)
        {
            startInfo.Environment[key] = value;
        }

        startInfo.Environment["IntoChatOS__Home"] = candidateHome;
        startInfo.Environment["IntoChatOS__Modules__Flutter__Host"] = "None";
        startInfo.Environment["IntoChatOS__Topology"] = "local";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort}";
        startInfo.Environment[ModuleConfiguration.EnvironmentName(AuthKeys.Posture)] = "Open";
        startInfo.Environment[ModuleConfiguration.EnvironmentName(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true";
        startInfo.Environment[IntoChatOSNames.MasterKeyEnvironmentVariable] = Secret();
        startInfo.Environment["OTEL_SERVICE_NAME"] = $"candidate-{sha}";

        CandidateProcessHandle? process = null;
        try
        {
            process = _launcher(startInfo);
            var tracked = new TrackedProcess(sha, process, httpPort, candidateHome, WorktreeOf(root, sha));
            if (!_tracked.TryAdd(sha, tracked))
            {
                StopAndDispose(process);
                throw new CandidateHostException("run", $"Candidate '{sha}' is already running on this host.");
            }

            var endpoint = $"http://127.0.0.1:{httpPort}";
            return new RunningCandidate(sha, new CandidateEndpoints(endpoint, endpoint), $"proc-{sha}");
        }
        catch
        {
            if (process is not null)
            {
                StopAndDispose(process);
            }

            TryDeleteDirectory(candidateHome);
            throw;
        }
    }

    public async Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        Checked(sha);
        if (!_tracked.TryGetValue(sha, out var tracked))
        {
            throw new CandidateHostException("healthy", $"{sha} is not running on this host");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(budget);
        var healthUri = new Uri($"http://127.0.0.1:{tracked.HttpPort}/health");
        try
        {
            while (true)
            {
                if (tracked.Process.HasExited)
                {
                    var stderr = tracked.Process.StandardErrorSnapshot.Trim();
                    var stdout = tracked.Process.StandardOutputSnapshot.Trim();
                    var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                    throw new CandidateHostException(
                        "healthy",
                        $"proc-{sha} exited with code {tracked.Process.ExitCode} before becoming healthy. {detail}".Trim());
                }

                try
                {
                    using var response = await _http.GetAsync(healthUri, limit.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Candidate still binding loopback listener; check exit state and retry.
                }

                if (tracked.Process.HasExited)
                {
                    var stderr = tracked.Process.StandardErrorSnapshot.Trim();
                    var stdout = tracked.Process.StandardOutputSnapshot.Trim();
                    var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                    throw new CandidateHostException(
                        "healthy",
                        $"proc-{sha} exited with code {tracked.Process.ExitCode} before becoming healthy. {detail}".Trim());
                }

                await Task.Delay(HealthPollInterval, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"proc-{sha} was not healthy within {budget}.");
        }
    }

    public async Task<string?> Commit(string sha, CancellationToken ct)
    {
        Checked(sha);
        try
        {
            return await Git(await _workdir(ct).ConfigureAwait(false)).Advance(DockerCandidateHost.LiveRef, sha, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("commit", error.Message);
        }
    }

    public async Task Uncommit(string sha, string? previous, CancellationToken ct)
    {
        Checked(sha);
        try
        {
            await Git(await _workdir(ct).ConfigureAwait(false)).Retreat(DockerCandidateHost.LiveRef, sha, previous, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("commit", error.Message);
        }
    }

    public async Task Remove(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var homeDir = Path.GetFullPath(Path.Combine(root, "homes", sha));
        var worktreeDir = WorktreeOf(root, sha);

        if (_tracked.TryRemove(sha, out var tracked))
        {
            homeDir = tracked.HomeDirectory;
            worktreeDir = tracked.WorktreeDirectory;
            await StopAndConfirmExitedAsync(tracked.Process, ct).ConfigureAwait(false);
            tracked.Process.Dispose();
        }

        if (Directory.Exists(homeDir))
        {
            await DeleteDirectoryWithRetryAsync(homeDir, ct).ConfigureAwait(false);
        }

        if (Directory.Exists(worktreeDir))
        {
            await Git(root).RemoveWorktree(worktreeDir, ct).ConfigureAwait(false);
        }
    }

    public async Task<string[]> Orphans(string[] known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(known);
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var orphans = _tracked.Keys.Where(sha => !knownSet.Contains(sha)).ToArray();
        foreach (var sha in orphans)
        {
            await Remove(sha, ct).ConfigureAwait(false);
        }

        return orphans;
    }

    internal bool TryGetTracked(string sha, out TrackedProcess? tracked) => _tracked.TryGetValue(sha, out tracked);

    private int AllocateSafeLoopbackPort()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = _portAllocator();
            if (candidate is LiveHttpPort or LiveMcpPort)
            {
                continue;
            }

            if (_tracked.Values.Any(t => t.HttpPort == candidate))
            {
                continue;
            }

            return candidate;
        }

        throw new CandidateHostException("run", "Unable to allocate a free non-live loopback TCP port for candidate.");
    }

    private static int AllocateEphemeralLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void EnsureNotLiveHome(string candidateHome)
    {
        var userProfile = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        var liveHome = Path.GetFullPath(Path.Combine(userProfile, ".intochat"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidateHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(normalizedCandidate, liveHome, comparison)
            || normalizedCandidate.StartsWith(liveHome + Path.DirectorySeparatorChar, comparison))
        {
            throw new CandidateHostException(
                "run",
                $"Candidate home '{normalizedCandidate}' must not be equal to or inside live home '{liveHome}'.");
        }
    }

    private static async Task StopAndConfirmExitedAsync(CandidateProcessHandle process, CancellationToken ct)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exitCts.CancelAfter(ProcessExitTimeout);
            try
            {
                await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    throw new CandidateHostException("remove", $"Candidate process {process.Id} did not exit within {ProcessExitTimeout}.");
                }
            }
        }

        if (!process.HasExited)
        {
            throw new CandidateHostException("remove", $"Candidate process {process.Id} is still running after kill.");
        }
    }

    private static readonly ILogger Log = OsConsoleLog.For<ProcessCandidateHost>();

    private static void StopAndDispose(CandidateProcessHandle process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Best-effort kill of candidate process failed during startup rollback.");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.LogWarning(ex, "Failed to clear file attributes on '{File}' before cleanup.", file);
                    }
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Best-effort cleanup of candidate directory '{Path}' failed.", path);
        }
    }

    private async Task<string> Dotnet(string stage, string worktree, CancellationToken ct, params string[] args)
    {
        var result = await _processes.RunAsync("dotnet", args, worktree, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new CandidateHostException(stage, $"dotnet {string.Join(' ', args)}:\n{Tail(result)}");
        }

        return result.Output;
    }

    private static string Tail(ProcessResult result, int lines = 40)
    {
        var all = (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(lines));
    }

    private static async Task<string> Workdir(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var snapshot = await integrations.Get("aspire").ReadAsync(ct).ConfigureAwait(false);
        return snapshot.Settings.TryGetValue(AspireModule.WorkdirField, out var workdir) && workdir is { Length: > 0 }
            ? Path.GetFullPath(workdir)
            : throw new CandidateHostException("prepare", "the aspire Integration has no Workdir");
    }

    private static async Task<CandidateSecrets> Secrets(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var released = await integrations.Get("aspire").ReleaseAsync(ct).ConfigureAwait(false);
        var model = released.Values.TryGetValue(AspireModule.CandidateModelField, out var json) && json is { Length: > 0 }
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : [];
        return new CandidateSecrets(model);
    }

    private Git Git(string root) => new(_processes, Path.GetDirectoryName(Path.GetFullPath(root))!);
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static string WorktreeOf(string root, string sha) => Path.Combine(root, "worktrees", sha);

    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    private static void Checked(string sha)
    {
        if (sha is null || !Sha().IsMatch(sha))
        {
            throw new CandidateHostException("sha", $"'{sha}' is not a commit sha");
        }
    }
}
