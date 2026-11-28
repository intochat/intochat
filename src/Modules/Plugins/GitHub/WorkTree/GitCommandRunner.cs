using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain.Log;
using IntoChat.OS.Host.Execution;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

internal sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Unified hardened subprocess runner for <c>git</c>, <c>gh</c>, and <c>dotnet format whitespace</c>.
/// Scrubs custody/presence environment secrets, disables interactive prompts, supports ephemeral
/// <c>GIT_CONFIG_*</c> auth header injection with token redaction, and supports per-instance
/// <see cref="WorkTreeOptions.ToolPath"/> overrides without mutating global <c>PATH</c>.
/// </summary>
internal sealed partial class GitCommandRunner
{
    private readonly WorkTreeOptions _options;
    private readonly ILogger _logger;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessOutput>> _processExecutor;

    public GitCommandRunner(
        WorkTreeOptions options,
        ILogger logger,
        Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processExecutor = null)
    {
        _options = options;
        _logger = logger;
        _processExecutor = processExecutor is null
            ? (psi, timeout, ct) => ExecuteProcessAsync(psi, timeout, _logger, ct)
            : async (psi, _, ct) =>
            {
                var (exitCode, stdOut, stdErr) = await processExecutor(psi, ct).ConfigureAwait(false);
                return new ProcessOutput(exitCode, stdOut, stdErr);
            };
    }

    public Task<ProcessOutput> RunSourceGitAsync(
        string sourcePath,
        bool allowLocalFileTransport,
        CancellationToken ct,
        params string[] gitSubArgs)
        => RunProcessAsync(
            "git",
            BuildSourceGitArgs(sourcePath, allowLocalFileTransport, gitSubArgs),
            workingDirectory: sourcePath,
            ct: ct);

    public Task<ProcessOutput> RunLockedGitAsync(
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

    /// <summary>
    /// Uploads local commit objects to GitHub over HTTPS using a short-lived bearer token passed
    /// exclusively via ephemeral <c>GIT_CONFIG_*</c> process environment (never CLI args, never
    /// <c>.git/config</c>, never logs or exception messages).
    /// </summary>
    public async Task PushWithEphemeralTokenAsync(
        GitHubRepository repository,
        string worktreePath,
        string branchName,
        string commitSha,
        bool force,
        string bearerToken,
        CancellationToken ct)
    {
        ValidateGitPushInputs(branchName, commitSha);
        var validatedWorktreePath = ValidateLocalWorktreePath(worktreePath);

        var basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{bearerToken}"));
        var remoteUrl = $"https://github.com/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}.git";

        var args = new List<string>(24)
        {
            "-C", validatedWorktreePath,
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null",
            "-c", "protocol.file.allow=never",
            "-c", "http.sslVerify=true",
            "-c", "http.proxy=",
            "-c", "http.https://github.com/.proxy=",
            "-c", "credential.helper=",
            "-c", "core.askPass=",
            "push",
            "--no-verify",
        };
        if (force)
        {
            args.Add("--force");
        }

        args.Add("--");
        args.Add(remoteUrl);
        args.Add($"{commitSha}:refs/heads/{branchName}");

        var extraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basicAuth}",
        };

        ProcessOutput output;
        try
        {
            output = await RunProcessAsync("git", args, validatedWorktreePath, ct, extraEnvironment).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new GitHubUnavailableException($"Native git push timed out after {_options.CommandTimeout.TotalSeconds:0.#}s and the child process was terminated.");
        }

        if (output.ExitCode != 0)
        {
            var rawDetail = string.IsNullOrWhiteSpace(output.StdErr) ? output.StdOut.Trim() : output.StdErr.Trim();
            var redacted = RedactToken(rawDetail, bearerToken, basicAuth);
            throw new GitHubUnavailableException($"Git push to '{repository.Owner}/{repository.Name}' failed (exit {output.ExitCode}): {redacted}");
        }
    }

    public async Task<ProcessOutput> RunGhFromEmptyDirAsync(CancellationToken ct, params string[] arguments)
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "intochat-gh-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            return await RunProcessAsync("gh", arguments, workingDirectory: emptyDir, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(emptyDir, recursive: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "Best-effort cleanup of empty gh working directory.");
            }
        }
    }

    public async Task VerifyCSharpFormattingAsync(string treeRoot, IReadOnlyList<RepoPath> changedPaths, CancellationToken ct)
    {
        if (!_options.VerifyFormatting)
        {
            return;
        }

        var existingCSharp = changedPaths
            .Where(static p => p.IsCSharp)
            .Select(static p => p.Value)
            .Where(rel => File.Exists(Path.Combine(treeRoot, rel.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        if (existingCSharp.Length == 0)
        {
            return;
        }

        var args = new List<string>(existingCSharp.Length + 6)
        {
            "format",
            "whitespace",
            ".",
            "--folder",
            "--verify-no-changes",
            "--verbosity",
            "minimal",
            "--include",
        };
        args.AddRange(existingCSharp);

        var result = await RunProcessAsync("dotnet", args, workingDirectory: treeRoot, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = result.StdOut.Trim();
            }

            throw new InvalidOperationException(
                $"Pre-approval quality gate failed `dotnet format whitespace --verify-no-changes`: {detail}");
        }
    }

    public static void EnsureSuccess(ProcessOutput output, string action)
    {
        if (output.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(output.StdErr) ? output.StdOut.Trim() : output.StdErr.Trim();
            throw new InvalidOperationException($"Failed to {action} (exit {output.ExitCode}): {detail}");
        }
    }

    internal static void ValidateGitPushInputs(string branchName, string commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha) || !CommitShaPattern().IsMatch(commitSha))
        {
            throw new ArgumentException("CommitSha must be a 40-character SHA-1 or 64-character SHA-256 hex digest.", nameof(commitSha));
        }

        if (!IsValidGitBranchName(branchName))
        {
            throw new ArgumentException($"Branch name '{branchName}' is not a valid git ref.", nameof(branchName));
        }
    }

    internal static bool IsValidGitBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return false;
        }

        if (branchName[0] is '-' or '.' or '/' or '+'
            || branchName.EndsWith('/')
            || branchName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || branchName.Contains("..", StringComparison.Ordinal)
            || branchName.Contains("//", StringComparison.Ordinal)
            || branchName.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in branchName)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\u007f'
                || ch is ':' or '~' or '^' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        foreach (var segment in branchName.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment.EndsWith('.') || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static string ValidateLocalWorktreePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath))
        {
            throw new ArgumentException("LocalWorktreePath must be a fully qualified path.", nameof(rawPath));
        }

        var fullPath = Path.GetFullPath(rawPath);
        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException($"LocalWorktreePath '{fullPath}' does not exist.", nameof(rawPath));
        }

        var dotGit = Path.Combine(fullPath, ".git");
        if (!File.Exists(dotGit) && !Directory.Exists(dotGit))
        {
            throw new ArgumentException($"LocalWorktreePath '{fullPath}' is not a git repository or worktree (missing .git).", nameof(rawPath));
        }

        return fullPath;
    }

    internal static string RedactToken(string text, string bearer, string basicAuth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        if (!string.IsNullOrEmpty(bearer))
        {
            result = result.Replace(bearer, "[REDACTED]", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(basicAuth))
        {
            result = result.Replace(basicAuth, "[REDACTED]", StringComparison.Ordinal);
        }

        return result;
    }

    internal static async Task<ProcessOutput> ExecuteProcessAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        ILogger? logger,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);
            return new ProcessOutput(process.ExitCode, stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogSwallowed(ex, "Best-effort kill on timed-out/cancelled git/gh/format process.");
            }

            try
            {
                await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger?.LogSwallowed(ex, "Draining killed git/gh/format stdout/stderr streams.");
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Process '{psi.FileName}' timed out after {timeout.TotalSeconds:0.#}s and the child process was terminated.");
        }
    }

    private Task<ProcessOutput> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var effectivePath = BuildEffectivePath();
        var resolvedExecutable = string.IsNullOrWhiteSpace(_options.ToolPath)
            ? fileName
            : ResolveExecutable(fileName, effectivePath);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExecutable,
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

        ChildEnvironmentPolicy.Scrub(psi);

        if (!string.IsNullOrWhiteSpace(effectivePath))
        {
            psi.Environment["PATH"] = effectivePath;
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        return _processExecutor(psi, _options.CommandTimeout, ct);
    }

    private string? BuildEffectivePath()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(_options.ToolPath))
        {
            return currentPath;
        }

        return string.IsNullOrWhiteSpace(currentPath)
            ? _options.ToolPath
            : _options.ToolPath + Path.PathSeparator + currentPath;
    }

    private static string ResolveExecutable(string fileName, string? pathValue)
    {
        if (Path.IsPathRooted(fileName) || string.IsNullOrWhiteSpace(pathValue))
        {
            return fileName;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", string.Empty]
            : [string.Empty];

        foreach (var rawDir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            foreach (var ext in extensions)
            {
                var candidateName = fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext;
                var candidate = Path.Combine(dir, candidateName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
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

    [GeneratedRegex(@"^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();
}
