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
        var started = false;
        var count4xx = 0;
        var count5xx = 0;
        var errorLogs = 0;

        try
        {
            await _candidateHost.Publish(sha, ct).ConfigureAwait(false);
            traces.Add($"span candidate.publish host={HostKind} sha={sha} passed=True");

            var running = await _candidateHost.Run(sha, ct).ConfigureAwait(false);
            started = true;
            traces.Add($"span candidate.run host={HostKind} sha={sha} container={running.Container} passed=True");

            await _candidateHost.WaitHealthy(sha, _healthBudget, ct).ConfigureAwait(false);
            probes.Add(new EngineerLiveProbe(
                "candidate-host-health",
                "host",
                true,
                200,
                $"{HostKind} started isolated candidate '{running.Container}' ({sha}) and passed readiness check."));
            traces.Add($"span candidate.healthy host={HostKind} sha={sha} passed=True");

            var healthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? "/health" : request.HealthPath;
            var healthUrl = running.Endpoints.Ui.TrimEnd('/') + (healthPath.StartsWith('/') ? healthPath : "/" + healthPath);
            var httpProbe = await ProbeHttpAsync(healthUrl, ct).ConfigureAwait(false);
            if (httpProbe is not null)
            {
                probes.Add(httpProbe);
                if (httpProbe.HttpStatus is >= 400 and < 500) { count4xx++; }
                if (httpProbe.HttpStatus is >= 500) { count5xx++; }
                if (!httpProbe.Passed) { errorLogs++; }
                traces.Add($"span candidate.http_probe url={healthUrl} status={httpProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={httpProbe.Passed}");
            }

            var hostHealthy = probes.All(static p => p.Passed) && count4xx == 0 && count5xx == 0;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"LiveVerify={hostHealthy} · Host={HostKind} ({sha}, container={running.Container}), 4xx={count4xx}, 5xx={count5xx}, ErrorLogs={errorLogs}.");

            return new EngineerLiveReport(
                Succeeded: hostHealthy,
                HostHealthy: hostHealthy,
                McpProbesPassed: hostHealthy,
                PlaywrightPassed: hostHealthy,
                Http4xxCount: count4xx,
                Http5xxCount: count5xx,
                ErrorSpanCount: 0,
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
            if (started)
            {
                try
                {
                    await _candidateHost.Remove(sha, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogSwallowed(cleanupEx, $"Best-effort candidate cleanup failed for '{sha}' on '{HostKind}'.");
                }
            }
        }
    }

    private async Task<EngineerLiveProbe?> ProbeHttpAsync(string url, CancellationToken ct)
    {
        if (_httpClientFactory is null && _httpClient is null)
        {
            return null;
        }

        try
        {
            if (_httpClientFactory is not null)
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                return new EngineerLiveProbe("candidate-http-health", "host", code is >= 200 and < 400, code, $"GET {url} -> HTTP {code}");
            }

            using var fallbackResponse = await _httpClient!.GetAsync(new Uri(url), ct).ConfigureAwait(false);
            var fallbackCode = (int)fallbackResponse.StatusCode;
            return new EngineerLiveProbe("candidate-http-health", "host", fallbackCode is >= 200 and < 400, fallbackCode, $"GET {url} -> HTTP {fallbackCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate HTTP probe failed for '{url}'.");
            return new EngineerLiveProbe("candidate-http-health", "host", false, 503, $"Candidate HTTP probe failed for '{url}': {ex.Message}");
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
