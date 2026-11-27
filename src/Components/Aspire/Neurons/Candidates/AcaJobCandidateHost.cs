using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static IntoChat.Aspire.Candidates.CandidateWorkspace;

namespace IntoChat.Aspire.Candidates;

internal sealed class DefaultAzureAcaJobTokenProvider : IAcaJobTokenProvider
{
    private readonly TokenCredential _credential;

    public DefaultAzureAcaJobTokenProvider()
        : this(new DefaultAzureCredential())
    {
    }

    internal DefaultAzureAcaJobTokenProvider(TokenCredential credential)
        => _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public async Task<string> GetTokenAsync(string armScope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(armScope);
        var token = await _credential.GetTokenAsync(new TokenRequestContext([armScope]), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
        {
            throw new CandidateHostException("auth", "Azure Managed Identity returned an empty ARM access token.");
        }

        return token.Token;
    }
}

// Cloud candidate host backed by policy-compliant Azure Container Apps Consumption Jobs (Microsoft.App/jobs).
// Enforces Policy Rule B ceilings, strict ARM host/identifier validation (SSRF prevention), Managed Identity auth,
// and secret-scrubbed execution environment overrides via CandidateGitWorkspace.
internal sealed partial class AcaJobCandidateHost : ICandidateHost, IDisposable
{
    public const string CandidateShaEnvKey = "INTOCHAT_CANDIDATE_SHA";
    public const string ContainerHomePath = "/home/agent";
    public const string ContainerIntoChatHomePath = "/home/agent/intochat-home";

    private static readonly ILogger Log = OsConsoleLog.For<AcaJobCandidateHost>();

    private readonly IConfiguration _live;
    private readonly AcaJobCandidateOptions _options;
    private readonly Uri _armBaseUri;
    private readonly string _armScope;
    private readonly string _jobBasePath;
    private readonly Func<CancellationToken, Task<string>> _workdir;
    private readonly Func<CancellationToken, Task<CandidateSecrets>> _secrets;
    private readonly CandidateGitWorkspace _workspace;
    private readonly IAcaJobTokenProvider _tokenProvider;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, string> _executionBySha = new(StringComparer.Ordinal);
    private int _disposed;

    public AcaJobCandidateHost(
        IModuleIntegrations<AspireModule> integrations,
        IConfiguration configuration,
        IProcessRunner processes,
        IOptions<AspireOptions> options,
        IAcaJobTokenProvider tokenProvider)
        : this(
            processes,
            configuration,
            options?.Value.AcaJob ?? throw new ArgumentNullException(nameof(options)),
            ct => Workdir(integrations, ct),
            tokenProvider,
            http: null,
            secrets: ct => Secrets(integrations, ct))
    {
    }

    internal AcaJobCandidateHost(
        IProcessRunner processes,
        IConfiguration configuration,
        AcaJobCandidateOptions options,
        Func<CancellationToken, Task<string>> workdir,
        IAcaJobTokenProvider tokenProvider,
        HttpClient? http = null,
        Func<CancellationToken, Task<CandidateSecrets>>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _live = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _armBaseUri = _options.ValidateAndGetArmBaseUri();
        _armScope = $"{_armBaseUri.Scheme}://{_armBaseUri.Host}/.default";
        _jobBasePath = $"/subscriptions/{_options.SubscriptionId}/resourceGroups/{Uri.EscapeDataString(_options.ResourceGroup)}/providers/Microsoft.App/jobs/{Uri.EscapeDataString(_options.JobName)}";
        _workdir = workdir ?? throw new ArgumentNullException(nameof(workdir));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _secrets = secrets ?? (_ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())));
        _workspace = new CandidateGitWorkspace(processes, _workdir);

        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            _ownsHttpClient = true;
        }
        else
        {
            _http = http;
            _ownsHttpClient = false;
        }
    }

    public IReadOnlyDictionary<string, string> LastRunEnvironment { get; private set; } = new Dictionary<string, string>();

    public Task<PreparedCandidate> Prepare(
        string baseSha,
        string summary,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
        => _workspace.Prepare(baseSha, summary, files, ct);

    public async Task BuildGate(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        string[] changed;
        try
        {
            changed = await _workspace.ChangedFiles(root, worktree, "HEAD~1", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("gate", error.Message);
        }

        var projects = changed
            .Select(file => ProjectOf(worktree, file))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var project in projects)
        {
            await _workspace.Dotnet("gate", worktree, ct, "build", project).ConfigureAwait(false);
        }

        var tests = projects
            .Select(project => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(project)!)!, "tests"))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetDirectories(folder).Where(dir => Directory.EnumerateFiles(dir, "*.csproj").Any()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var test in tests)
        {
            await _workspace.Dotnet("gate", worktree, ct, "test", "--project", test).ConfigureAwait(false);
        }
    }

    public async Task<string> Publish(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var build = Path.Combine(root, "builds", sha);
        Directory.CreateDirectory(build);
        return build;
    }

    public async Task<RunningCandidate> Run(string sha, CancellationToken ct)
    {
        Checked(sha);
        var given = await _secrets(ct).ConfigureAwait(false);
        var environment = BuildExecutionEnvironment(sha, given.Model);
        LastRunEnvironment = environment;

        var startUri = BuildArmUri($"{_jobBasePath}/start");
        var payload = BuildStartPayload(environment);
        using var response = await SendArmAsync(HttpMethod.Post, startUri, payload, "run", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CandidateHostException("run", $"ACA Job start failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        var executionName = ParseExecutionName(body);
        _executionBySha[sha] = executionName;
        var executionUri = BuildArmUri($"{_jobBasePath}/executions/{executionName}").AbsoluteUri;
        return new RunningCandidate(sha, new CandidateEndpoints(executionUri, executionUri), executionName);
    }

    public async Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        Checked(sha);
        if (!_executionBySha.TryGetValue(sha, out var executionName))
        {
            throw new CandidateHostException("healthy", $"{sha} is not running on ACA Job '{_options.JobName}'.");
        }

        var effectiveBudget = budget > TimeSpan.FromSeconds(_options.ReplicaTimeoutSeconds)
            ? TimeSpan.FromSeconds(_options.ReplicaTimeoutSeconds)
            : budget;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(effectiveBudget);
        var pollUri = BuildArmUri($"{_jobBasePath}/executions/{executionName}");
        try
        {
            while (true)
            {
                using var response = await SendArmAsync(HttpMethod.Get, pollUri, jsonBody: null, "healthy", limit.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CandidateHostException("healthy", $"ACA Job execution poll failed ({(int)response.StatusCode}): {Truncate(body)}");
                }

                var status = ParseExecutionStatus(body);
                if (string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Degraded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CandidateHostException("healthy", $"ACA Job execution '{executionName}' entered terminal status '{status}'.");
                }

                await Task.Delay(_options.PollInterval, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ACA Job execution '{executionName}' did not reach Running/Succeeded within {effectiveBudget}.");
        }
    }

    public Task<string?> Commit(string sha, CancellationToken ct)
        => _workspace.Commit(sha, ct);

    public Task Uncommit(string sha, string? previous, CancellationToken ct)
        => _workspace.Uncommit(sha, previous, ct);

    public async Task Remove(string sha, CancellationToken ct)
    {
        Checked(sha);
        if (!_executionBySha.TryGetValue(sha, out var executionName))
        {
            return;
        }

        await StopExecutionAsync(executionName, "remove", ct).ConfigureAwait(false);
        _executionBySha.TryRemove(new KeyValuePair<string, string>(sha, executionName));
    }

    public async Task<string[]> Orphans(string[] known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(known);
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var knownExecutions = _executionBySha
            .Where(pair => knownSet.Contains(pair.Key))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var listUri = BuildArmUri($"{_jobBasePath}/executions");
        using var response = await SendArmAsync(HttpMethod.Get, listUri, jsonBody: null, "orphans", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CandidateHostException("orphans", $"ACA Job executions list failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        var removed = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !ExecutionNamePattern().IsMatch(name))
            {
                continue;
            }

            var status = item.TryGetProperty("properties", out var props) && props.TryGetProperty("status", out var stEl)
                ? stEl.GetString()
                : null;
            var isActive = string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase);
            if (!isActive || knownExecutions.Contains(name))
            {
                continue;
            }

            var candidateSha = TryExtractCandidateSha(item);
            if (candidateSha is not null && knownSet.Contains(candidateSha))
            {
                continue;
            }

            await StopExecutionAsync(name, "orphans", ct).ConfigureAwait(false);
            removed.Add(name);
            if (candidateSha is not null)
            {
                _executionBySha.TryRemove(candidateSha, out _);
            }
        }

        return [.. removed];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    internal Dictionary<string, string> BuildExecutionEnvironment(
        string sha,
        IReadOnlyDictionary<string, string> modelSecrets)
    {
        var environment = CandidateGitWorkspace.CopySafeHostConfiguration(_live, rewriteLoopbackForContainer: true);
        foreach (var (key, value) in modelSecrets)
        {
            if (ChildEnvironmentPolicy.IsHostCustodySecret(key)
                || string.Equals(key, PresenceProof.TokenHashEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            environment[key] = value;
        }

        environment[CandidateShaEnvKey] = sha;
        environment["HOME"] = ContainerHomePath;
        environment["XDG_CONFIG_HOME"] = $"{ContainerHomePath}/.config";
        environment["XDG_DATA_HOME"] = $"{ContainerHomePath}/.local/share";
        environment["XDG_CACHE_HOME"] = $"{ContainerHomePath}/.cache";
        environment["TMPDIR"] = $"{ContainerHomePath}/tmp";
        environment["DOTNET_CLI_HOME"] = ContainerHomePath;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        environment[CandidateGitWorkspace.Env(IntoChatOSNames.HomeKey)] = ContainerIntoChatHomePath;
        environment[CandidateGitWorkspace.Env(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true";

        return environment;
    }

    private string BuildStartPayload(IReadOnlyDictionary<string, string> environment)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("containers");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("name", _options.ContainerName);
            if (!string.IsNullOrWhiteSpace(_options.ImageOverride))
            {
                writer.WriteString("image", _options.ImageOverride.Trim());
            }

            writer.WritePropertyName("resources");
            writer.WriteStartObject();
            writer.WriteNumber("cpu", _options.Cpu);
            writer.WriteString("memory", _options.Memory.Trim());
            writer.WriteEndObject();

            writer.WritePropertyName("env");
            writer.WriteStartArray();
            foreach (var pair in environment.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", pair.Key);
                writer.WriteString("value", pair.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task StopExecutionAsync(string executionName, string stage, CancellationToken ct)
    {
        var stopUri = BuildArmUri($"{_jobBasePath}/executions/{executionName}/stop");
        using var response = await SendArmAsync(HttpMethod.Post, stopUri, jsonBody: null, stage, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.LogWarning("ACA Job execution stop returned {StatusCode} for {Execution}: {Detail}", (int)response.StatusCode, executionName, Truncate(body));
            throw new CandidateHostException(stage, $"ACA Job stop failed ({(int)response.StatusCode}): {Truncate(body)}");
        }
    }

    private async Task<HttpResponseMessage> SendArmAsync(
        HttpMethod method,
        Uri uri,
        string? jsonBody,
        string stage,
        CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(_armScope, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CandidateHostException(stage, $"ARM request to '{uri.AbsolutePath}' failed: {ex.Message}");
        }
    }

    private Uri BuildArmUri(string relativePath)
        => new(_armBaseUri, $"{relativePath}?api-version={Uri.EscapeDataString(_options.ApiVersion)}");

    private static string ParseExecutionName(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var name = doc.RootElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !ExecutionNamePattern().IsMatch(name))
            {
                throw new CandidateHostException("run", "ACA Job start response returned a missing or invalid execution name.");
            }

            return name;
        }
        catch (JsonException ex)
        {
            throw new CandidateHostException("run", $"ACA Job start response was not valid JSON: {ex.Message}");
        }
    }

    private static string ParseExecutionStatus(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("properties", out var props)
                && props.TryGetProperty("status", out var statusEl)
                && statusEl.ValueKind == JsonValueKind.String)
            {
                return statusEl.GetString() ?? "Unknown";
            }

            return "Unknown";
        }
        catch (JsonException ex)
        {
            throw new CandidateHostException("healthy", $"ACA Job execution poll response was not valid JSON: {ex.Message}");
        }
    }

    private static string? TryExtractCandidateSha(JsonElement executionItem)
    {
        if (!executionItem.TryGetProperty("properties", out var props)
            || !props.TryGetProperty("template", out var template)
            || !template.TryGetProperty("containers", out var containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var container in containers.EnumerateArray())
        {
            if (!container.TryGetProperty("env", out var envVars) || envVars.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var env in envVars.EnumerateArray())
            {
                var key = env.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(key, CandidateShaEnvKey, StringComparison.Ordinal))
                {
                    return env.TryGetProperty("value", out var v) ? v.GetString() : null;
                }
            }
        }

        return null;
    }

    private static string Truncate(string value)
        => string.IsNullOrEmpty(value) ? "(empty)" : value.Length <= 400 ? value : value[..400] + "...";

    private static string? ProjectOf(string worktree, string file)
    {
        var full = Path.GetFullPath(worktree);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(full, file))); dir is not null && dir.Length > full.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault() is { } project)
            {
                return project;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionNamePattern();
}
