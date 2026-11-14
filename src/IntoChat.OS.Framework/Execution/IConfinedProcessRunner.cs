// os-only: OS process confinement contracts for Tier 2 tool and coder execution.
namespace IntoChat.OS.Framework.Execution;

/// <summary>Outbound network posture applied to a confined OS child process.</summary>
public enum NetworkPolicy
{
    /// <summary>Zero network capabilities: blocks both internet and private/loopback sockets.</summary>
    None = 0,

    /// <summary>Outbound public internet allowed while private LAN and loopback remain blocked where supported.</summary>
    PublicInternetOnly = 1,
}

/// <summary>Effective OS isolation mechanism applied to a confined child process.</summary>
public enum ConfinementMode
{
    /// <summary>Windows Job Object (KILL_ON_JOB_CLOSE + memory/process/UI limits + handle allowlist + clean env).</summary>
    JobObject = 0,

    /// <summary>Windows AppContainer token + Job Object + explicit ACL grants + handle allowlist.</summary>
    AppContainer = 1,

    /// <summary>Windows Less-Privileged AppContainer (opts out of ALL APPLICATION PACKAGES) + Job Object.</summary>
    LessPrivilegedAppContainer = 2,

    /// <summary>Linux Bubblewrap unprivileged user/mount/PID/IPC/net namespace confinement.</summary>
    Bubblewrap = 3,

    /// <summary>macOS Seatbelt sandbox-exec S-expression profile confinement.</summary>
    Seatbelt = 4,

    /// <summary>POSIX process group + sanitized environment fallback when bwrap/sandbox-exec is unavailable.</summary>
    PosixSanitizedProcess = 5,
}

/// <summary>Specification for launching a confined external tool, compiler, or sandbox worker process.</summary>
public sealed record ConfinementSpec(
    string SandboxId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyList<string> ReadOnlyPaths,
    IReadOnlyList<string> ReadWritePaths,
    IReadOnlyDictionary<string, string> CleanEnvironment,
    NetworkPolicy Network = NetworkPolicy.None,
    long MaxMemoryBytes = 1024L * 1024 * 1024,
    int MaxProcesses = 16,
    TimeSpan Timeout = default,
    bool PreferAppContainer = false,
    bool UseLessPrivilegedAppContainer = false)
{
    public TimeSpan EffectiveTimeout => Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : Timeout;
}

/// <summary>Completed outcome of a confined child process execution.</summary>
public sealed record ConfinedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration,
    ConfinementMode EffectiveMode);

/// <summary>Executes an external process inside an OS-enforced confinement boundary with zero inherited host secrets.</summary>
public interface IConfinedProcessRunner
{
    Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default);
}
