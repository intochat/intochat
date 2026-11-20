using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

public enum SandboxExecutionMode
{
    Auto = 0,
    Container = 1,
    ConfinedHost = 2,
}

public enum SandboxNetworkPolicy
{
    None = 0,
    Bridge = 1,
    Host = 2,
}

public sealed class SandboxCommandOptions
{
    public SandboxExecutionMode Mode { get; set; } = SandboxExecutionMode.Auto;

    public SandboxNetworkPolicy NetworkPolicy { get; set; } = SandboxNetworkPolicy.None;

    public string ContainerRuntime { get; set; } = "docker";

    public string? ContainerImage { get; set; }

    public string ContainerWorkspaceMount { get; set; } = "/workspace";

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public string? ToolPath { get; set; }
}

public sealed record SandboxCommandRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? RelativeWorkingDirectory = null,
    SandboxNetworkPolicy? NetworkPolicy = null,
    TimeSpan? Timeout = null);

public sealed record SandboxCommandResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    SandboxExecutionMode ExecutedMode,
    IReadOnlyList<string> EffectiveInvocation,
    long DurationMs)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Executes build, analysis, and test commands for <see cref="WorkTreeBackendKind.ContainerVolume"/> worktree leases
/// inside an isolated container volume mount when a container runtime is configured/available, or inside the confined
/// host workspace directory during in-process runs without Docker.
/// </summary>
public interface ISandboxCommandRunner
{
    static ISandboxCommandRunner CreateDefault(
        SandboxCommandOptions? options = null,
        IConfiguration? configuration = null,
        ILogger? logger = null)
        => new SandboxCommandRunner(options, configuration, logger);

    IReadOnlyList<string> BuildContainerArguments(
        WorkTreeLease lease,
        SandboxCommandRequest request);

    Task<SandboxCommandResult> ExecuteAsync(
        WorkTreeLease lease,
        SandboxCommandRequest request,
        CancellationToken ct = default);
}
