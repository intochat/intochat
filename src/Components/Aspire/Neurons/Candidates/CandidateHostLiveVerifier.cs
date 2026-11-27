using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Candidates;

/// <summary>
/// Wires Plane 2 <c>EngineerLiveVerifier</c> to the configured <see cref="ICandidateHost"/>
/// (<see cref="ProcessCandidateHost"/> or <see cref="AgentDevSlotsCandidateHost"/> locally),
/// failing closed whenever unconfigured or whenever candidate startup, readiness, or HTTP probing fails.
/// </summary>
internal sealed class CandidateHostLiveVerifier : IEngineerCandidateHostVerifier
{
    private static readonly TimeSpan DefaultHealthBudget = TimeSpan.FromMinutes(2);

    private readonly ICandidateHost? _candidateHost;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly TimeSpan _healthBudget;
    private readonly ILogger _logger;

    public CandidateHostLiveVerifier(
        ICandidateHost? candidateHost = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CandidateHostLiveVerifier>? logger = null)
        : this(candidateHost, httpClientFactory, httpClient: null, DefaultHealthBudget, logger)
    {
    }

    internal CandidateHostLiveVerifier(
        ICandidateHost? candidateHost,
        IHttpClientFactory? httpClientFactory,
        HttpClient? httpClient,
        TimeSpan healthBudget,
        ILogger? logger = null)
    {
        _candidateHost = candidateHost;
        _httpClientFactory = httpClientFactory;
        _httpClient = httpClient;
        _healthBudget = healthBudget > TimeSpan.Zero ? healthBudget : DefaultHealthBudget;
        _logger = logger ?? OsConsoleLog.For<CandidateHostLiveVerifier>();
    }

    public bool IsConfigured => _candidateHost is not null;

    public string HostKind => _candidateHost?.GetType().Name ?? "Unconfigured";

    public async Task<EngineerLiveReport> VerifyCandidateAsync(
        CandidateLiveVerificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_candidateHost is null)
        {
            const string detail = "Candidate host is not configured; fail-closed live verification refused.";
            return new EngineerLiveReport(
                Succeeded: false,
                HostHealthy: false,
                McpProbesPassed: false,
                PlaywrightPassed: false,
                Http4xxCount: 0,
                Http5xxCount: 1,
                ErrorSpanCount: 0,
                ErrorLogCount: 1,
                Probes: [new EngineerLiveProbe("candidate-host-required", "host", false, 503, detail)],
                TraceSummaries: ["span candidate.host status=unconfigured passed=False"],
                Summary: $"LiveVerify=False · {detail}");
        }

        var sha = NormalizeCandidateSha(request.CommitSha, request.WorktreePath);
        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var published = false;
        var started = false;
        var count4xx = 0;
        var count5xx = 0;
        var errorLogs = 0;

        try
        {
            var stagedFiles = ReadStagedTouchedFiles(request.WorktreePath, request.ChangedFiles);
            if (stagedFiles.Count > 0 || string.IsNullOrWhiteSpace(request.CommitSha))
            {
                var prepared = await _candidateHost.Prepare(sha, request.Goal, stagedFiles, ct).ConfigureAwait(false);
                sha = prepared.Sha;
                published = true;
                traces.Add($"span candidate.prepare host={HostKind} sha={sha} files={stagedFiles.Count} passed=True");
            }

            published = true;
            await _candidateHost.Publish(sha, ct).ConfigureAwait(false);
            traces.Add($"span candidate.publish host={HostKind} sha={sha} passed=True");

            started = true;
            var running = await _candidateHost.Run(sha, ct).ConfigureAwait(false);
            traces.Add($"span candidate.run host={HostKind} sha={sha} container={running.Container} passed=True");

            await _candidateHost.WaitHealthy(sha, _healthBudget, ct).ConfigureAwait(false);
            probes.Add(new EngineerLiveProbe(
                "candidate-host-health",
                "host",
                true,
                200,
                $"{HostKind} started isolated candidate '{running.Container}' ({sha}) and passed readiness check."));
            traces.Add($"span candidate.healthy host={HostKind} sha={sha} passed=True");

            var rawHealthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? "/health" : request.HealthPath;
            if (!TryValidateRelativeHealthPath(rawHealthPath, out var validHealthPath, out var healthPathError))
            {
                var badPathProbe = new EngineerLiveProbe("candidate-http-health", "host", false, 400, healthPathError!);
                probes.Add(badPathProbe);
                count4xx++;
                errorLogs++;
                traces.Add($"span candidate.http_probe path=invalid passed=False");
                return new EngineerLiveReport(
                    Succeeded: false,
                    HostHealthy: false,
                    McpProbesPassed: false,
                    PlaywrightPassed: false,
                    Http4xxCount: count4xx,
                    Http5xxCount: count5xx,
                    ErrorSpanCount: 1,
                    ErrorLogCount: errorLogs,
                    Probes: [.. probes],
                    TraceSummaries: [.. traces],
                    Summary: $"LiveVerify=False · {healthPathError}");
            }

            var uiHealthUrl = running.Endpoints.Ui.TrimEnd('/') + validHealthPath;
            var uiProbe = await ProbeHttpAsync("candidate-http-health", "host", uiHealthUrl, ct).ConfigureAwait(false);
            probes.Add(uiProbe);
            if (uiProbe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (uiProbe.HttpStatus is >= 500) { count5xx++; }
            if (!uiProbe.Passed) { errorLogs++; }
            traces.Add($"span candidate.http_probe url={uiHealthUrl} status={uiProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={uiProbe.Passed}");

            var mcpHealthUrl = !string.IsNullOrWhiteSpace(running.Endpoints.Mcp)
                ? running.Endpoints.Mcp.TrimEnd('/') + validHealthPath
                : uiHealthUrl;
            var mcpProbe = await ProbeHttpAsync("candidate-mcp-health", "mcp", mcpHealthUrl, ct).ConfigureAwait(false);
            probes.Add(mcpProbe);
            if (mcpProbe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (mcpProbe.HttpStatus is >= 500) { count5xx++; }
            if (!mcpProbe.Passed) { errorLogs++; }
            traces.Add($"span candidate.mcp_probe url={mcpHealthUrl} status={mcpProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={mcpProbe.Passed}");

            var hostHealthy = uiProbe.Passed && count4xx == 0 && count5xx == 0;
            var mcpProbesPassed = mcpProbe.Passed;
            var uiSurfacePassed = uiProbe.Passed;
            var succeeded = hostHealthy && mcpProbesPassed && uiSurfacePassed;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"LiveVerify={succeeded} · Host={HostKind} ({sha}, container={running.Container}), 4xx={count4xx}, 5xx={count5xx}, ErrorLogs={errorLogs}.");

            return new EngineerLiveReport(
                Succeeded: succeeded,
                HostHealthy: hostHealthy,
                McpProbesPassed: mcpProbesPassed,
                PlaywrightPassed: uiSurfacePassed,
                Http4xxCount: count4xx,
                Http5xxCount: count5xx,
                ErrorSpanCount: succeeded ? 0 : 1,
                ErrorLogCount: errorLogs,
                Probes: [.. probes],
                TraceSummaries: [.. traces],
                Summary: summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate host '{HostKind}' verification failed for '{sha}'.");
            var stage = ex is CandidateHostException che ? che.Stage : "verify";
            var detail = $"{HostKind} failed at stage '{stage}' for '{sha}': {ex.Message}";
            probes.Add(new EngineerLiveProbe("candidate-host-startup", "host", false, 500, detail));
            traces.Add($"span candidate.{stage} host={HostKind} sha={sha} status=ERROR error={ex.GetType().Name}");
            return new EngineerLiveReport(
                Succeeded: false,
                HostHealthy: false,
                McpProbesPassed: false,
                PlaywrightPassed: false,
                Http4xxCount: count4xx,
                Http5xxCount: Math.Max(1, count5xx),
                ErrorSpanCount: 1,
                ErrorLogCount: Math.Max(1, errorLogs),
                Probes: [.. probes],
                TraceSummaries: [.. traces],
                Summary: $"LiveVerify=False · {detail}");
        }
        finally
        {
            if (published || started)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _candidateHost.Remove(sha, cleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogSwallowed(cleanupEx, $"Best-effort candidate cleanup failed for '{sha}' on '{HostKind}'.");
                }
            }
        }
    }

    internal static bool TryValidateRelativeHealthPath(string? rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            normalizedPath = "/health";
            return true;
        }

        if (!rawPath.StartsWith('/')
            || rawPath.StartsWith("//", StringComparison.Ordinal)
            || rawPath.Contains("://", StringComparison.Ordinal)
            || rawPath.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            error = $"Invalid candidate HealthPath '{rawPath}': must be a single-slash relative path without control characters, whitespace, or scheme.";
            return false;
        }

        normalizedPath = rawPath;
        return true;
    }

    private static IReadOnlyDictionary<string, string> ReadStagedTouchedFiles(string? worktreePath, IReadOnlyList<string>? touchedFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(worktreePath) || touchedFiles is not { Count: > 0 } || !Directory.Exists(worktreePath))
        {
            return result;
        }

        var fullRoot = Path.GetFullPath(worktreePath);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        foreach (var rel in touchedFiles)
        {
            if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel))
            {
                continue;
            }

            var candidatePath = Path.GetFullPath(Path.Combine(fullRoot, rel));
            if (!candidatePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidatePath))
            {
                continue;
            }

            result[rel.Replace('\\', '/')] = File.ReadAllText(candidatePath);
        }

        return result;
    }

    private async Task<EngineerLiveProbe> ProbeHttpAsync(string probeName, string category, string url, CancellationToken ct)
    {
        if (_httpClientFactory is null && _httpClient is null)
        {
            return new EngineerLiveProbe(
                probeName,
                category,
                false,
                503,
                $"No HttpClient or IHttpClientFactory configured to probe '{url}'; fail-closed policy refused.");
        }

        try
        {
            if (_httpClientFactory is not null)
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                return new EngineerLiveProbe(probeName, category, code is >= 200 and < 400, code, $"GET {url} -> HTTP {code}");
            }

            using var fallbackResponse = await _httpClient!.GetAsync(new Uri(url), ct).ConfigureAwait(false);
            var fallbackCode = (int)fallbackResponse.StatusCode;
            return new EngineerLiveProbe(probeName, category, fallbackCode is >= 200 and < 400, fallbackCode, $"GET {url} -> HTTP {fallbackCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate HTTP probe failed for '{url}'.");
            return new EngineerLiveProbe(probeName, category, false, 503, $"Candidate HTTP probe failed for '{url}': {ex.Message}");
        }
    }

    internal static string NormalizeCandidateSha(string? commitSha, string worktreePath)
    {
        if (!string.IsNullOrWhiteSpace(commitSha))
        {
            var trimmed = commitSha.Trim().ToLowerInvariant();
            if (trimmed.Length >= 7 && trimmed.All(static c => char.IsAsciiHexDigit(c)))
            {
                return trimmed.Length > 12 ? trimmed[..12] : trimmed;
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(worktreePath ?? "candidate")))[..12];
    }
}
