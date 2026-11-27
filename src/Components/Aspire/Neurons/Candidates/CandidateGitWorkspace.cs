using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using static IntoChat.Aspire.Candidates.CandidateWorkspace;

namespace IntoChat.Aspire.Candidates;

// Shared git worktree lifecycle and secret-scrubbed host configuration copy used by both
// AgentDevSlotsCandidateHost and ProcessCandidateHost.
internal sealed class CandidateGitWorkspace
{
    // Promotion advances this ref, never a branch someone has checked out; people fast-forward their branch from it.
    public const string LiveRef = "refs/intochat-os/live";

    internal const string OtlpHttpEndpoint = IntoChat.Aspire.CandidateEnvironment.OtlpHttpEndpoint;
    internal const string AspNetCoreEnvironmentKey = "ASPNETCORE_ENVIRONMENT";
    internal const string OtlpExporterEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string OtlpExporterProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    internal static readonly string[] ExportCadence = ["OTEL_BSP_SCHEDULE_DELAY", "OTEL_BLRP_SCHEDULE_DELAY", "OTEL_METRIC_EXPORT_INTERVAL"];

    private static readonly string[] CopiedSections =
    [
        ModuleConfiguration.ModulesSectionName + ":",
        IdentityHostOptions.SectionName + ":",
    ];

    private static readonly string[] AmbientSecretExactNames =
    [
        "HOME",
        "USERPROFILE",
        "SSH_AUTH_SOCK",
        "SSH_AGENT_PID",
        "GIT_ASKPASS",
        "GIT_CONFIG_GLOBAL",
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "NUGET_API_KEY",
        "DOCKER_AUTH_CONFIG",
    ];

    private static readonly string[] AmbientSecretPrefixes =
    [
        "AWS_",
        "AZURE_",
        "GITHUB_",
        "SSH_",
    ];

    private readonly IProcessRunner _processes;
    private readonly Func<CancellationToken, Task<string>> _workdir;

    public CandidateGitWorkspace(IProcessRunner processes, Func<CancellationToken, Task<string>> workdir)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _workdir = workdir ?? throw new ArgumentNullException(nameof(workdir));
    }

    public async Task<PreparedCandidate> Prepare(
        string baseSha,
        string summary,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
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
            var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var (path, content) in files)
            {
                var target = Path.GetFullPath(Path.Combine(staging, path));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
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

    public async Task<string?> Commit(string sha, CancellationToken ct)
    {
        Checked(sha);
        try
        {
            var root = await _workdir(ct).ConfigureAwait(false);
            return await Git(root).Advance(LiveRef, sha, ct).ConfigureAwait(false);
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
            var root = await _workdir(ct).ConfigureAwait(false);
            await Git(root).Retreat(LiveRef, sha, previous, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("commit", error.Message);
        }
    }

    public Task<string[]> ChangedFiles(string root, string worktree, string baseSha, CancellationToken ct)
        => Git(root).ChangedFiles(worktree, baseSha, ct);

    public Task RemoveWorktree(string root, string worktree, CancellationToken ct)
        => Git(root).RemoveWorktree(worktree, ct);

    public async Task<string> Dotnet(string stage, string worktree, CancellationToken ct, params string[] args)
    {
        var result = await _processes.RunAsync("dotnet", args, worktree, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new CandidateHostException(stage, $"dotnet {string.Join(' ', args)}:\n{Tail(result)}");
        }

        return result.Output;
    }

    public static Dictionary<string, string> CopySafeHostConfiguration(
        IConfiguration live,
        bool rewriteLoopbackForContainer = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        var environment = live.AsEnumerable()
            .Where(pair => pair.Value is not null && Travels(pair.Key))
            .ToDictionary(pair => ModuleConfiguration.EnvironmentName(pair.Key), pair => pair.Value!, StringComparer.Ordinal);
        environment["ASPNETCORE_ENVIRONMENT"] = live["ASPNETCORE_ENVIRONMENT"] ?? "Development";
        environment["DOTNET_ENVIRONMENT"] = live["DOTNET_ENVIRONMENT"] ?? live["ASPNETCORE_ENVIRONMENT"] ?? "Development";
        environment[Env(AuthKeys.Posture)] = "Open";
        if (rewriteLoopbackForContainer)
        {
            environment[Env(AuthKeys.AllowOpenOnNetwork)] = "true";
        }

        if (live[OtlpHttpEndpoint] is { Length: > 0 } otlpHttp)
        {
            environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = rewriteLoopbackForContainer ? FromContainer(otlpHttp) : otlpHttp;
            environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        }
        else if (live["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } otlp)
        {
            environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = rewriteLoopbackForContainer ? FromContainer(otlp) : otlp;
            if (live["OTEL_EXPORTER_OTLP_PROTOCOL"] is { Length: > 0 } protocol)
            {
                environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol;
            }
        }

        if (live["OTEL_EXPORTER_OTLP_HEADERS"] is { Length: > 0 } headers)
        {
            environment["OTEL_EXPORTER_OTLP_HEADERS"] = headers;
        }

        foreach (var cadence in ExportCadence)
        {
            if (live[cadence] is { Length: > 0 } value)
            {
                environment[cadence] = value;
            }
        }

        ScrubAmbientHostSecrets(environment);
        return environment;
    }

    public static void ScrubAmbientHostSecrets(Dictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ChildEnvironmentPolicy.ScrubCustodyAndSecretVariables(environment);
        var toRemove = environment.Keys
            .Where(key =>
                AmbientSecretExactNames.Any(exact => string.Equals(key, exact, StringComparison.OrdinalIgnoreCase))
                || AmbientSecretPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var key in toRemove)
        {
            environment.Remove(key);
        }
    }

    internal static string Env(string key) => ModuleConfiguration.EnvironmentName(key);

    internal static string IntegrationVariable(string id, string field)
        => ModuleConfiguration.IntegrationEnvironmentName(id, field);

    private Git Git(string root) => new(_processes, Path.GetDirectoryName(Path.GetFullPath(root))!);

    private static bool Travels(string key)
        => CopiedSections.Any(section => key.StartsWith(section, StringComparison.OrdinalIgnoreCase));

    private static string FromContainer(string url)
        => url.Replace("localhost", "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            .Replace("127.0.0.1", "host.docker.internal", StringComparison.Ordinal);
}
