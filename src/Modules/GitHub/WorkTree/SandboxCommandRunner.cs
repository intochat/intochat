using System.Diagnostics;
using System.Security;
using System.Text;
using DigitalBrain.Log;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

internal sealed class SandboxCommandRunner : ISandboxCommandRunner
{
    private const string DefaultContainerImage = "mcr.microsoft.com/dotnet/sdk:11.0-preview";
    private static readonly SandboxCommandOptions DefaultOptions = new();

    private readonly SandboxCommandOptions _options;
    private readonly IConfiguration? _configuration;
    private readonly ILogger _logger;

    public SandboxCommandRunner(
        SandboxCommandOptions? options = null,
        IConfiguration? configuration = null,
        ILogger? logger = null)
    {
        _options = options ?? DefaultOptions;
        _configuration = configuration;
        _logger = logger ?? OsConsoleLog.For<SandboxCommandRunner>();
    }

    public IReadOnlyList<string> BuildContainerArguments(
        WorkTreeLease lease,
        SandboxCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        ValidateExecutable(request.Executable);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        var hostWorkspace = ResolveWorkspaceRoot(lease);
        var mountRoot = NormalizeContainerMount(_options.ContainerWorkspaceMount);
        var containerWorkDir = ResolveContainerWorkingDirectory(mountRoot, request.RelativeWorkingDirectory);
        var network = ResolveNetworkFlag(request.NetworkPolicy ?? ResolveConfiguredNetworkPolicy());
        var image = ResolveConfiguredImage() ?? DefaultContainerImage;

        var args = new List<string>(request.Arguments.Count + 20)
        {
            "run",
            "--rm",
            "--network",
            network,
            "--security-opt",
            "no-new-privileges",
            "-v",
            $"{hostWorkspace}:{mountRoot}:rw",
            "-w",
            containerWorkDir,
            "-e",
            "HOME=/tmp",
            "-e",
            "DOTNET_CLI_HOME=/tmp/dotnet",
            "-e",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
            "-e",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-e",
            "GIT_TERMINAL_PROMPT=0",
            image,
            request.Executable.Trim(),
        };

        foreach (var rawArg in request.Arguments)
        {
            args.Add(TranslateHostPathToContainerMount(rawArg, hostWorkspace, mountRoot));
        }

        return args;
    }

    public async Task<SandboxCommandResult> ExecuteAsync(
        WorkTreeLease lease,
        SandboxCommandRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        ValidateExecutable(request.Executable);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        var hostWorkspace = ResolveWorkspaceRoot(lease);
        if (!Directory.Exists(hostWorkspace))
        {
            throw new DirectoryNotFoundException($"Sandbox workspace directory does not exist: {hostWorkspace}");
        }

        var dotGitPath = Path.Combine(hostWorkspace, ".git");
        if (File.Exists(dotGitPath) || Directory.Exists(dotGitPath))
        {
            await WorkTreeService.VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(false);
        }

        var hostWorkDir = ResolveHostWorkingDirectory(hostWorkspace, request.RelativeWorkingDirectory);
        var timeout = request.Timeout ?? _options.DefaultTimeout;
        var effectiveMode = await SelectExecutionModeAsync(ct).ConfigureAwait(false);

        if (effectiveMode == SandboxExecutionMode.Container)
        {
            var runtime = ResolveContainerRuntime();
            var containerArgs = BuildContainerArguments(lease, request);
            IReadOnlyList<string> invocation = [runtime, .. containerArgs];
            var sw = Stopwatch.StartNew();
            var output = await RunProcessAsync(runtime, containerArgs, hostWorkspace, timeout, ct).ConfigureAwait(false);
            sw.Stop();
            return new SandboxCommandResult(
                output.ExitCode,
                output.StdOut,
                output.StdErr,
                SandboxExecutionMode.Container,
                invocation,
                sw.ElapsedMilliseconds);
        }

        var hostArgs = NormalizeHostArguments(request.Arguments, hostWorkspace, hostWorkDir);
        IReadOnlyList<string> hostInvocation = [request.Executable.Trim(), .. hostArgs];
        var hostSw = Stopwatch.StartNew();
        var hostOutput = await RunProcessAsync(request.Executable.Trim(), hostArgs, hostWorkDir, timeout, ct).ConfigureAwait(false);
        hostSw.Stop();

        return new SandboxCommandResult(
            hostOutput.ExitCode,
            hostOutput.StdOut,
            hostOutput.StdErr,
            SandboxExecutionMode.ConfinedHost,
            hostInvocation,
            hostSw.ElapsedMilliseconds);
    }

    private async Task<SandboxExecutionMode> SelectExecutionModeAsync(CancellationToken ct)
    {
        var configuredMode = ResolveConfiguredMode();
        if (configuredMode is SandboxExecutionMode.Container or SandboxExecutionMode.ConfinedHost)
        {
            return configuredMode;
        }

        var configuredImage = ResolveConfiguredImage();
        if (string.IsNullOrWhiteSpace(configuredImage))
        {
            return SandboxExecutionMode.ConfinedHost;
        }

        var runtime = ResolveContainerRuntime();
        return await ProbeContainerRuntimeAsync(runtime, ct).ConfigureAwait(false)
            ? SandboxExecutionMode.Container
            : SandboxExecutionMode.ConfinedHost;
    }

    private async Task<bool> ProbeContainerRuntimeAsync(string runtime, CancellationToken ct)
    {
        try
        {
            var probe = await RunProcessAsync(
                runtime,
                ["version", "--format", "{{.Server.Version}}"],
                Path.GetTempPath(),
                TimeSpan.FromSeconds(3),
                ct).ConfigureAwait(false);
            return probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogSwallowed(ex, "Container runtime probe did not find an active daemon; using confined host execution.");
            return false;
        }
    }

    private async Task<ProcessOutput> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var effectivePath = BuildEffectivePath();
        var resolvedExecutable = ResolveExecutable(executable, effectivePath);

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
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start sandbox command '{executable}'.");
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

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
            catch (Exception killEx) when (killEx is not OperationCanceledException)
            {
                _logger.LogSwallowed(killEx, "Best-effort process tree termination on sandbox command timeout or cancellation.");
            }

            if (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Sandbox command '{executable}' timed out after {timeout.TotalSeconds:0.#}s.");
            }

            throw;
        }
    }

    private SandboxExecutionMode ResolveConfiguredMode()
    {
        if (_options.Mode != SandboxExecutionMode.Auto)
        {
            return _options.Mode;
        }

        var raw = _configuration?["IntoChatOS:Engineer:SandboxMode"]
            ?? _configuration?["IntoChatOS:WorkTree:SandboxMode"];
        return Enum.TryParse<SandboxExecutionMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : SandboxExecutionMode.Auto;
    }

    private SandboxNetworkPolicy ResolveConfiguredNetworkPolicy()
    {
        if (_options.NetworkPolicy != SandboxNetworkPolicy.None)
        {
            return _options.NetworkPolicy;
        }

        var raw = _configuration?["IntoChatOS:Engineer:SandboxNetwork"]
            ?? _configuration?["IntoChatOS:WorkTree:SandboxNetwork"];
        return Enum.TryParse<SandboxNetworkPolicy>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : SandboxNetworkPolicy.None;
    }

    private string? ResolveConfiguredImage()
    {
        if (!string.IsNullOrWhiteSpace(_options.ContainerImage))
        {
            return _options.ContainerImage.Trim();
        }

        var cfg = _configuration?["IntoChatOS:Engineer:SandboxImage"]
            ?? _configuration?["IntoChatOS:WorkTree:SandboxImage"];
        return string.IsNullOrWhiteSpace(cfg) ? null : cfg.Trim();
    }

    private string ResolveContainerRuntime()
    {
        var cfg = _configuration?["IntoChatOS:Engineer:ContainerRuntime"]
            ?? _configuration?["IntoChatOS:WorkTree:ContainerRuntime"];
        if (!string.IsNullOrWhiteSpace(cfg))
        {
            return cfg.Trim();
        }

        return string.IsNullOrWhiteSpace(_options.ContainerRuntime) ? "docker" : _options.ContainerRuntime.Trim();
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
            ? [".exe", ".cmd", ".bat", string.Empty]
            : [string.Empty];

        foreach (var rawDir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            foreach (var ext in extensions)
            {
                var candidateName = ext.Length > 0 && fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? fileName
                    : fileName + ext;
                var candidate = Path.Combine(dir, candidateName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    private static void ValidateExecutable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var trimmed = executable.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Any(char.IsControl))
        {
            throw new SecurityException($"Sandbox command executable must be a bare tool name, got '{executable}'.");
        }
    }

    private static string ResolveWorkspaceRoot(WorkTreeLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.WorkspacePath);
        return Path.GetFullPath(lease.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeContainerMount(string? mount)
    {
        var clean = string.IsNullOrWhiteSpace(mount) ? "/workspace" : mount.Trim().Replace('\\', '/').TrimEnd('/');
        if (!clean.StartsWith('/'))
        {
            clean = "/" + clean;
        }

        return clean;
    }

    private static string ResolveContainerWorkingDirectory(string mountRoot, string? relativeWorkingDir)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDir))
        {
            return mountRoot;
        }

        var safe = RepoPath.Parse(relativeWorkingDir);
        return $"{mountRoot}/{safe.Value}";
    }

    private static string ResolveHostWorkingDirectory(string hostWorkspace, string? relativeWorkingDir)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDir))
        {
            return hostWorkspace;
        }

        var safe = RepoPath.Parse(relativeWorkingDir);
        return safe.ResolveUnder(hostWorkspace);
    }

    private static string ResolveNetworkFlag(SandboxNetworkPolicy policy)
        => policy switch
        {
            SandboxNetworkPolicy.Bridge => "bridge",
            SandboxNetworkPolicy.Host => "host",
            _ => "none",
        };

    private static string TranslateHostPathToContainerMount(string arg, string hostWorkspace, string mountRoot)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return arg;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = hostWorkspace + Path.DirectorySeparatorChar;
        var altPrefix = hostWorkspace.Replace('\\', '/') + "/";

        if (string.Equals(arg, hostWorkspace, comparison) || string.Equals(arg, hostWorkspace.Replace('\\', '/'), comparison))
        {
            return mountRoot;
        }

        if (arg.StartsWith(prefix, comparison))
        {
            var rel = arg[prefix.Length..].Replace('\\', '/');
            return $"{mountRoot}/{rel}";
        }

        if (arg.StartsWith(altPrefix, comparison))
        {
            var rel = arg[altPrefix.Length..].Replace('\\', '/');
            return $"{mountRoot}/{rel}";
        }

        return arg;
    }

    private static IReadOnlyList<string> NormalizeHostArguments(
        IReadOnlyList<string> arguments,
        string hostWorkspace,
        string hostWorkingDirectory)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (!string.IsNullOrWhiteSpace(arg)
                && !arg.StartsWith('-')
                && Path.IsPathRooted(arg))
            {
                var fullArg = Path.GetFullPath(arg);
                var workspacePrefix = hostWorkspace + Path.DirectorySeparatorChar;
                if (!string.Equals(fullArg, hostWorkspace, comparison) && !fullArg.StartsWith(workspacePrefix, comparison))
                {
                    throw new SecurityException($"Refusing host sandbox argument outside workspace root: '{arg}'.");
                }
            }

            normalized[i] = arg;
        }

        _ = hostWorkingDirectory;
        return normalized;
    }
}
