using System.Diagnostics;
using System.Text;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Execution;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

internal sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Hardened subprocess runner for <c>git</c>, <c>gh</c>, and <c>dotnet format whitespace</c>.
/// Scrubs custody/presence environment secrets, disables interactive prompts, and supports
/// per-instance <see cref="WorkTreeOptions.ToolPath"/> overrides without mutating global <c>PATH</c>.
/// </summary>
internal sealed class GitCommandRunner(WorkTreeOptions options, ILogger logger)
{
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
                logger.LogSwallowed(ex, "Best-effort cleanup of empty gh working directory.");
            }
        }
    }

    public async Task VerifyCSharpFormattingAsync(string treeRoot, IReadOnlyList<RepoPath> changedPaths, CancellationToken ct)
    {
        if (!options.VerifyFormatting)
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

    private async Task<ProcessOutput> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var effectivePath = BuildEffectivePath();
        var resolvedExecutable = ResolveExecutable(fileName, effectivePath);

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

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.CommandTimeout);

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
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, "Best-effort kill on timed-out/cancelled git/gh/format process.");
            }

            throw;
        }
    }

    private string? BuildEffectivePath()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(options.ToolPath))
        {
            return currentPath;
        }

        return string.IsNullOrWhiteSpace(currentPath)
            ? options.ToolPath
            : options.ToolPath + Path.PathSeparator + currentPath;
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
}
