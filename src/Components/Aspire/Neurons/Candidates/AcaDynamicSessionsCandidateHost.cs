using System.Collections.Concurrent;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static IntoChat.Aspire.Candidates.CandidateWorkspace;

namespace IntoChat.Aspire.Candidates;

/// <summary>
/// Plane 2 headless cloud fallback candidate host (Slice RFC-4: Azure Container Apps Dynamic Sessions / Kata MicroVMs).
/// Executes candidate build gates, publishing, isolated candidate runs, health verification, and orphan reclamation
/// against the Azure Dynamic Sessions pool management plane (<c>poolManagementEndpoint</c>) with Entra/Managed Identity
/// bearer authentication, per-candidate session-ID isolation, strict timeouts, secret-scrubbed environments, and
/// fail-closed behavior when unconfigured.
/// </summary>
internal sealed class AcaDynamicSessionsCandidateHost : ICandidateHost, IAsyncDisposable, IDisposable
{
    public const string LiveRef = DockerCandidateHost.LiveRef;
    public const string SessionHomePath = "/home/agent";
    public const string SessionIntoChatHomePath = "/home/agent/intochat-home";

    private static readonly string[] CopiedSections =
    [
        ModuleConfiguration.ModulesSectionName + ":",
        IdentityHostOptions.SectionName + ":",
    ];

    private static readonly ILogger Log = OsConsoleLog.For<AcaDynamicSessionsCandidateHost>();

    private readonly IProcessRunner _processes;
    private readonly IConfiguration _live;
    private readonly AcaDynamicSessionsClient _client;
    private readonly Func<CancellationToken, Task<string>> _workdir;
    private readonly Func<CancellationToken, Task<CandidateSecrets>> _secrets;
    private readonly TimeSpan _healthPollInterval;
    private readonly ConcurrentDictionary<string, ActiveCandidateSession> _sessionsBySha = new(StringComparer.Ordinal);
    private int _disposed;

    public AcaDynamicSessionsCandidateHost(
        IModuleIntegrations<AspireModule> integrations,
        IConfiguration configuration,
        IProcessRunner processes,
        IHttpClientFactory httpClientFactory,
        IAcaDynamicSessionsTokenProvider tokenProvider,
        IOptions<AspireOptions> options,
        ILogger<AcaDynamicSessionsClient>? clientLogger = null)
        : this(
            processes,
            configuration,
            new AcaDynamicSessionsClient(
                httpClientFactory,
                Options.Create((options ?? throw new ArgumentNullException(nameof(options))).Value.ToAcaDynamicSessionsOptions()),
                tokenProvider,
                clientLogger),
            ct => Workdir(integrations, ct),
            ct => Secrets(integrations, ct))
    {
    }

    internal AcaDynamicSessionsCandidateHost(
        IProcessRunner processes,
        IConfiguration configuration,
        AcaDynamicSessionsClient client,
        Func<CancellationToken, Task<string>> workdir,
        Func<CancellationToken, Task<CandidateSecrets>>? secrets = null,
        TimeSpan? healthPollInterval = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _live = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _workdir = workdir ?? throw new ArgumentNullException(nameof(workdir));
        _secrets = secrets ?? (_ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())));
        _healthPollInterval = healthPollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public bool IsConfigured => _client.IsConfigured;
    public IReadOnlyDictionary<string, string> LastRunEnvironment { get; private set; } = new Dictionary<string, string>();
    public string? LastSessionId { get; private set; }

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
        EnsureConfigured("gate");

        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        string[] changed;
        try
        {
            changed = await Git(root).ChangedFiles(worktree, "HEAD~1", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("gate", error.Message);
        }

        var sessionId = AcaSessionIsolation.BuildPlane2SessionId(sha, stage: "gate");
        LastSessionId = sessionId;
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_client.CurrentOptions.ExecutionTimeout.TotalSeconds));
        var request = new AcaExecutionRequest(
            Mode: "plane2-candidate-host",
            Action: "build-gate",
            TimeoutInSeconds: timeoutSeconds,
            CandidateSha: sha,
            ChangedFiles: changed,
            Environment: BuildScrubbedStageEnvironment(sha, sessionId, "gate"));

        try
        {
            var response = await _client.ExecuteAsync(sessionId, request, cancellationToken: ct).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw new CandidateHostException("gate", response.Error ?? $"ACA build gate failed for '{sha}' (status={response.Status}).");
            }
        }
        catch (AcaDynamicSessionsException error)
        {
            throw new CandidateHostException("gate", error.Message);
        }
        finally
        {
            await TryTerminateSessionAsync(sessionId, "gate").ConfigureAwait(false);
        }
    }

    public async Task<string> Publish(string sha, CancellationToken ct)
    {
        Checked(sha);
        EnsureConfigured("publish");

        var root = await _workdir(ct).ConfigureAwait(false);
        var sessionId = AcaSessionIsolation.BuildPlane2SessionId(sha, stage: "pub");
        LastSessionId = sessionId;
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_client.CurrentOptions.ExecutionTimeout.TotalSeconds));
        var request = new AcaExecutionRequest(
            Mode: "plane2-candidate-host",
            Action: "publish",
            TimeoutInSeconds: timeoutSeconds,
            CandidateSha: sha,
            Environment: BuildScrubbedStageEnvironment(sha, sessionId, "publish"));

        try
        {
            var response = await _client.ExecuteAsync(sessionId, request, cancellationToken: ct).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw new CandidateHostException("publish", response.Error ?? $"ACA publish failed for '{sha}' (status={response.Status}).");
            }

            return Path.Combine(root, "builds", sha);
        }
        catch (AcaDynamicSessionsException error)
        {
            throw new CandidateHostException("publish", error.Message);
        }
        finally
        {
            await TryTerminateSessionAsync(sessionId, "publish").ConfigureAwait(false);
        }
    }

    public async Task<RunningCandidate> Run(string sha, CancellationToken ct)
    {
        Checked(sha);
        EnsureConfigured("run");

        var given = await _secrets(ct).ConfigureAwait(false);
        var sessionId = AcaSessionIsolation.BuildPlane2SessionId(sha, stage: "run");
        LastSessionId = sessionId;

        var environment = BuildCandidateEnvironment(sha, sessionId, given.Model);
        LastRunEnvironment = environment;

        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_client.CurrentOptions.ExecutionTimeout.TotalSeconds));
        var request = new AcaExecutionRequest(
            Mode: "plane2-candidate-host",
            Action: "run-candidate",
            TimeoutInSeconds: timeoutSeconds,
            CandidateSha: sha,
            Environment: environment);

        try
        {
            var response = await _client.ExecuteAsync(sessionId, request, cancellationToken: ct).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw new CandidateHostException("run", response.Error ?? $"ACA candidate run failed for '{sha}' (status={response.Status}).");
            }

            var baseUri = _client.CurrentOptions.GetValidatedEndpointUri();
            var fallbackEndpoint = AcaDynamicSessionsClient
                .BuildSessionEndpointUri(baseUri, string.Empty, sessionId, _client.CurrentOptions.ApiVersion)
                .AbsoluteUri;
            var mcpUrl = string.IsNullOrWhiteSpace(response.McpEndpoint) ? fallbackEndpoint : response.McpEndpoint;
            var uiUrl = string.IsNullOrWhiteSpace(response.UiEndpoint) ? mcpUrl : response.UiEndpoint;

            var endpoints = new CandidateEndpoints(mcpUrl, uiUrl);
            _sessionsBySha[sha] = new ActiveCandidateSession(sha, sessionId, endpoints);
            return new RunningCandidate(sha, endpoints, sessionId);
        }
        catch (Exception error)
        {
            Log.LogWarning(error, "Candidate {Sha} failed during ACA Dynamic Sessions Run in session {SessionId}; rolling back.", sha, sessionId);
            await TryTerminateSessionAsync(sessionId, "run-rollback").ConfigureAwait(false);
            if (error is CandidateHostException or OperationCanceledException or TimeoutException)
            {
                throw;
            }

            throw new CandidateHostException("run", AcaPayloadSecretScrubber.RedactSecrets(error.Message));
        }
    }

    public async Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        Checked(sha);
        EnsureConfigured("healthy");

        if (!_sessionsBySha.TryGetValue(sha, out var active))
        {
            throw new CandidateHostException("healthy", $"{sha} is not running on this ACA Dynamic Sessions host");
        }

        var effectiveBudget = budget <= _client.CurrentOptions.SessionHealthTimeout
            ? budget
            : _client.CurrentOptions.SessionHealthTimeout;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(effectiveBudget);
        try
        {
            while (true)
            {
                try
                {
                    var probeTimeout = TimeSpan.FromSeconds(5);
                    if (await _client.CheckHealthAsync(active.SessionId, probeTimeout, limit.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (AcaDynamicSessionsException ex)
                {
                    Log.LogDebug(ex, "Candidate {Sha} in ACA session {SessionId} not answering health yet.", sha, active.SessionId);
                }
                catch (TimeoutException ex) when (!limit.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Log.LogDebug(ex, "Health probe timed out for candidate {Sha} in ACA session {SessionId}; retrying within budget.", sha, active.SessionId);
                }

                await Task.Delay(_healthPollInterval, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ACA session '{active.SessionId}' for candidate '{sha}' was not healthy within {effectiveBudget}.");
        }
    }

    public async Task<string?> Commit(string sha, CancellationToken ct)
    {
        Checked(sha);
        try
        {
            return await Git(await _workdir(ct).ConfigureAwait(false)).Advance(LiveRef, sha, ct).ConfigureAwait(false);
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
            await Git(await _workdir(ct).ConfigureAwait(false)).Retreat(LiveRef, sha, previous, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("commit", error.Message);
        }
    }

    public async Task Remove(string sha, CancellationToken ct)
    {
        Checked(sha);
        if (!_sessionsBySha.TryRemove(sha, out var active))
        {
            return;
        }

        if (!_client.IsConfigured)
        {
            return;
        }

        try
        {
            await _client.TerminateSessionAsync(active.SessionId, ct).ConfigureAwait(false);
        }
        catch (AcaDynamicSessionsException error)
        {
            throw new CandidateHostException("remove", error.Message);
        }
    }

    public async Task<string[]> Orphans(string[] known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(known);
        EnsureConfigured("orphans");

        AcaSessionInfo[] sessions;
        try
        {
            sessions = await _client.ListSessionsAsync(ct).ConfigureAwait(false);
        }
        catch (AcaDynamicSessionsException error)
        {
            throw new CandidateHostException("orphans", error.Message);
        }

        var removed = new List<string>();
        foreach (var session in sessions)
        {
            if (!AcaSessionIsolation.TryParsePlane2CandidateSha(session.SessionId, out var sha))
            {
                continue;
            }

            if (known.Contains(sha, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            await _client.TerminateSessionAsync(session.SessionId, ct).ConfigureAwait(false);
            _sessionsBySha.TryRemove(sha, out _);
            removed.Add(session.SessionId);
        }

        return [.. removed];
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_client.IsConfigured)
        {
            foreach (var entry in _sessionsBySha.ToArray())
            {
                if (_sessionsBySha.TryRemove(entry.Key, out var active))
                {
                    await TryTerminateSessionAsync(active.SessionId, "dispose").ConfigureAwait(false);
                }
            }
        }
        else
        {
            _sessionsBySha.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sessionsBySha.Clear();
    }

    internal Dictionary<string, string> BuildCandidateEnvironment(
        string sha,
        string sessionId,
        IReadOnlyDictionary<string, string> modelSecrets)
    {
        var ambient = CopySafeHostConfiguration();
        var environment = AcaPayloadSecretScrubber.ScrubEnvironment(ambient, modelSecrets);

        var orleansId = $"os-aca-{sha}";
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = "http://+:5080",
            ["HOME"] = SessionHomePath,
            ["XDG_CONFIG_HOME"] = $"{SessionHomePath}/.config",
            ["XDG_DATA_HOME"] = $"{SessionHomePath}/.local/share",
            ["XDG_CACHE_HOME"] = $"{SessionHomePath}/.cache",
            ["TMPDIR"] = $"{SessionHomePath}/tmp",
            ["DOTNET_CLI_HOME"] = SessionHomePath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["Orleans__ServiceId"] = orleansId,
            ["Orleans__ClusterId"] = orleansId,
            ["Orleans__EnableDistributedTracing"] = "true",
            ["OTEL_SERVICE_NAME"] = $"aca-{sessionId}",
            [DockerCandidateHost.Env(IntoChatOSNames.HomeKey)] = SessionIntoChatHomePath,
            [DockerCandidateHost.Env(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true",
            // Fresh per-candidate ephemeral master key — never the live host master key.
            [IntoChatOSNames.MasterKeyEnvironmentVariable] = Secret(),
        };

        foreach (var (key, value) in overrides)
        {
            environment[key] = value;
        }

        environment.Remove(ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleOptionsSection("ai") + ":Default:Transcription"));
        return environment;
    }

    private static Dictionary<string, string> BuildScrubbedStageEnvironment(string sha, string sessionId, string stage)
        => new(StringComparer.Ordinal)
        {
            ["HOME"] = SessionHomePath,
            ["DOTNET_CLI_HOME"] = SessionHomePath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["OTEL_SERVICE_NAME"] = $"aca-{stage}-{sessionId}",
            ["INTOCHAT_CANDIDATE_SHA"] = sha,
        };

    private Dictionary<string, string> CopySafeHostConfiguration()
    {
        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _live.AsEnumerable())
        {
            if (value is null)
            {
                continue;
            }

            if (!CopiedSections.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            copied[DockerCandidateHost.Env(key)] = value;
        }

        return copied;
    }

    private void EnsureConfigured(string stage)
    {
        try
        {
            _client.EnsureConfigured();
        }
        catch (AcaDynamicSessionsNotConfiguredException ex)
        {
            throw new CandidateHostException(stage, ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new CandidateHostException(stage, ex.Message);
        }
    }

    private async Task TryTerminateSessionAsync(string sessionId, string reason)
    {
        try
        {
            await _client.TerminateSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.LogWarning(error, "Best-effort ACA session teardown failed for '{SessionId}' ({Reason}).", sessionId, reason);
        }
    }

    private Git Git(string root) => new(_processes, Path.GetDirectoryName(Path.GetFullPath(root))!);

    private sealed record ActiveCandidateSession(string Sha, string SessionId, CandidateEndpoints Endpoints);
}
