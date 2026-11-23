using DigitalBrain.Log;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DigitalBrain.State;
using Microsoft.Extensions.Configuration;

namespace IntoChat.Aspire;

[ExportableState]
internal sealed record AspireTelemetryState(
    AspireResourceView[] Resources,
    AspireLogEntry[] StructuredLogs,
    AspireTraceSpan[] Traces)
{
    public AspireTelemetryState() : this([], [], [])
    {
    }
}

internal static partial class AspireLiveTelemetry
{
    private const int MaxLiveSpans = 200;
    private static readonly ConcurrentQueue<AspireTraceSpan> LiveSpans = new();
    private static int _listenerInitialized;

    public static void EnsureListener()
    {
        if (Interlocked.Exchange(ref _listenerInitialized, 1) != 0)
        {
            return;
        }

        var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.StartsWith("IntoChat", StringComparison.OrdinalIgnoreCase)
                || source.Name.StartsWith("DigitalBrain", StringComparison.OrdinalIgnoreCase)
                || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase),
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = static activity =>
            {
                var status = activity.Status == ActivityStatusCode.Error
                    ? "Error"
                    : "Ok";
                var error = activity.Status == ActivityStatusCode.Error
                    ? Sanitize(activity.StatusDescription ?? activity.GetTagItem("error.type")?.ToString())
                    : null;
                var resource = string.IsNullOrWhiteSpace(activity.Source.Name) ? KernelNames.Os : activity.Source.Name;
                LiveSpans.Enqueue(new AspireTraceSpan(
                    activity.TraceId.ToString(),
                    activity.SpanId.ToString(),
                    resource,
                    activity.DisplayName ?? activity.OperationName,
                    status,
                    (long)Math.Round(activity.Duration.TotalMilliseconds),
                    error));

                while (LiveSpans.Count > MaxLiveSpans && LiveSpans.TryDequeue(out _))
                {
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public static AspireTraceSpan[] SnapshotSpans() => [.. LiveSpans];

    public static AspireResourceView[] DiscoverResources(AspireResourceView[] baseline, IConfiguration? configuration)
    {
        var list = new List<AspireResourceView>();
        var osUrl = FirstEndpoint(
            configuration?["ASPNETCORE_URLS"],
            configuration?["INTOCHAT_OS_URL"],
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
            Environment.GetEnvironmentVariable("INTOCHAT_OS_URL"))
            ?? baseline.FirstOrDefault(r => KernelNames.IsKernel(r.Name))?.Endpoint;

        if (!string.IsNullOrWhiteSpace(osUrl))
        {
            list.Add(new AspireResourceView(KernelNames.Os, "Running", StripUrlCredentials(osUrl)));
        }

        foreach (var item in baseline)
        {
            if (KernelNames.IsKernel(item.Name))
            {
                continue;
            }
            list.Add(item with { Endpoint = string.IsNullOrEmpty(item.Endpoint) ? string.Empty : StripUrlCredentials(item.Endpoint) });
        }

        var otlpEndpoint = FirstEndpoint(
            configuration?["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"],
            configuration?["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"],
            configuration?["OTEL_EXPORTER_OTLP_ENDPOINT"],
            Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"),
            Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"),
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

        if (!string.IsNullOrWhiteSpace(otlpEndpoint)
            && !list.Any(r => string.Equals(r.Name, "otlp-telemetry", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new AspireResourceView("otlp-telemetry", "Configured", StripUrlCredentials(otlpEndpoint)));
        }

        var dashboardUrl = FirstEndpoint(
            configuration?["DOTNET_DASHBOARD_URLS"],
            Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_URLS"));

        if (!string.IsNullOrWhiteSpace(dashboardUrl)
            && !list.Any(r => string.Equals(r.Name, "aspire-dashboard", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new AspireResourceView("aspire-dashboard", "Running", StripUrlCredentials(dashboardUrl)));
        }

        var ollamaUrl = FirstEndpoint(
            configuration?["IntoChatOS:Modules:ai:Ollama:Endpoint"],
            Environment.GetEnvironmentVariable("IntoChatOS__Modules__ai__Ollama__Endpoint"),
            Environment.GetEnvironmentVariable("OLLAMA_HOST"));

        if (!string.IsNullOrWhiteSpace(ollamaUrl)
            && !list.Any(r => string.Equals(r.Name, "ollama", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new AspireResourceView("ollama", "Configured", StripUrlCredentials(ollamaUrl)));
        }

        return [.. list];
    }

    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        var step1 = UrlCredsRegex().Replace(value, "$1[REDACTED]@");
        var step2 = TokenRegex().Replace(step1, "[REDACTED]");
        return KvSecretRegex().Replace(step2, "$1$2[REDACTED]");
    }

    private static string? FirstEndpoint(params string?[] candidates)
    {
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }
        return null;
    }

    private static string StripUrlCredentials(string endpoint)
        => UrlCredsRegex().Replace(endpoint, "$1");

    [GeneratedRegex(@"(https?://)[^/\s:@""']+:[^/\s@""']+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredsRegex();

    [GeneratedRegex(@"(?:Bearer\s+[A-Za-z0-9._\-/+]+=*|sk-(?:proj-|ant-(?:api\d{2}-)?)?[A-Za-z0-9_\-]{8,}|github_pat_[A-Za-z0-9_]{12,}|gh[pousr]_[A-Za-z0-9_]{8,}|AIza[0-9A-Za-z_\-]{20,}|xox[baprs]-[0-9A-Za-z\-]{10,}|vault://[A-Za-z0-9._\-/:]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?i)\b(password|pwd|client_secret|access_token|refresh_token|api_key|apikey)\s*([=:])\s*([^\s;,""'\}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex KvSecretRegex();
}

[DigitalBrain.Neurons.Neuron("aspire", State = "aspire")]
internal sealed class AspireNeuron : DigitalBrain.Neurons.Neuron<AspireTelemetryState>, IAspire
{
    private readonly IConfiguration? _configuration;
    private readonly IAspireTools? _mcp;

    private readonly DigitalBrain.Confirmation.IConfirmations? _confirmations;

    public AspireNeuron(IConfiguration? configuration = null, IAspireTools? mcp = null, DigitalBrain.Confirmation.IConfirmations? confirmations = null)
    {
        _confirmations = confirmations;
        _configuration = configuration;
        _mcp = mcp;
        AspireLiveTelemetry.EnsureListener();
    }

    // The live AppHost model when Aspire's MCP server is reachable; the configured view otherwise.
    public async Task<AspireResourceView[]> GetResourcesAsync()
        => _mcp is { Available: true }
            ? await _mcp.AllResources(CancellationToken.None)
            : AspireLiveTelemetry.DiscoverResources(State.Resources, _configuration);

    public async Task<string> Ask(string tool, string argumentsJson = "{}")
    {
        if (_mcp is not { Available: true }) { return "Aspire's MCP server is not available here; it is reachable only when the kernel runs under a local AppHost."; }
        if (!AspireMcp.ReadTools.Contains(tool)) { throw new ArgumentException($"'{tool}' is not one of the Aspire tools Ino may use: {string.Join(", ", AspireMcp.ReadTools)}."); }
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) ?? [];
        return await _mcp.Call(tool, arguments, CancellationToken.None);
    }

    // A direct person restarts at once; every other stamped caller waits for a person's yes. A refusal is always a returned value.
    public async Task<AspireRestartResult> Restart(string resourceName)
    {
        if (_mcp is not { Available: true }) { return new(resourceName ?? "", false, "unavailable"); }
        var live = await _mcp.AllResources(CancellationToken.None);
        var match = string.IsNullOrWhiteSpace(resourceName) ? null : live.FirstOrDefault(view => string.Equals(view.Name, resourceName, StringComparison.Ordinal));
        if (match is null) { return new(resourceName ?? "", false, "unknown", [.. live.Select(view => view.Name).Take(MaxKnown)]); }
        var name = match.Name;
        if (KernelNames.IsSelf(name)) { return new(name, false, "self"); }
        if (!IsDirectPerson(Context.Caller) && !await PersonSaysYes(name)) { return new(name, false, "declined"); }
#pragma warning disable CA1031 // a failed command is a returned value, never a throw
        try { await _mcp.Command(name, AspireMcp.RestartCommand, CancellationToken.None); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<AspireNeuron>().LogSwallowed(error, $"restarting the Aspire resource {name}");
            return new(name, false, "failed", Message: AspireLiveTelemetry.Sanitize(error.Message));
        }
#pragma warning restore CA1031
        await Publish(new AspireResourceRestarted(name, Context.Time.GetUtcNow()));
        return new(name, true);
    }

    private const int MaxKnown = 64;

    // The same rule Answerer.Of uses for person authority: a stamped person, direct, outside any conversation.
    private static bool IsDirectPerson(DigitalBrain.Access.Caller? caller)
        => caller is { Direct: true, Conversation: null, Actor: DigitalBrain.Access.Actor.Person };

    // Only an answer a person gave counts: a skipped plan (policy Never), a denial, a self-approval or an expiry is a no.
    private async Task<bool> PersonSaysYes(string name)
    {
        if (_confirmations is null) { return false; }
        var answer = await _confirmations.Confirm(new DigitalBrain.Confirmation.ConfirmationPlan(
            $"{RestartKind}:{name}:{Guid.NewGuid():N}", DigitalBrain.Confirmation.ConfirmationAction.Other, false,
            $"Restart {name}?", $"Restart the resource {name} of the running app.", "Restart", "Keep running", name)
        { RequiresPerson = true, Kind = RestartKind, Subject = name });
        return answer is { Approved: true, Skipped: false };
    }

    internal const string RestartKind = "aspire.resource.restart";

    public Task<string[]> GetConsoleLogsAsync(string resourceName, int tailLines = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        var lines = State.StructuredLogs
            .Where(log => KernelNames.Same(log.Resource, resourceName))
            .TakeLast(Math.Clamp(tailLines, 1, 500))
            .Select(log => $"[{log.Severity}] {log.Resource}: {AspireLiveTelemetry.Sanitize(log.Message)}"
                + (log.Exception is null ? "" : $" ({AspireLiveTelemetry.Sanitize(log.Exception)})"))
            .ToArray();
        return Task.FromResult(lines);
    }

    public Task<AspireLogEntry[]> GetStructuredLogsAsync(string? resourceName = null, string? minSeverity = null)
    {
        var query = State.StructuredLogs.Select(static log => log with
        {
            Message = AspireLiveTelemetry.Sanitize(log.Message) ?? string.Empty,
            Exception = AspireLiveTelemetry.Sanitize(log.Exception),
        });

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            query = query.Where(log => KernelNames.Same(log.Resource, resourceName));
        }

        if (!string.IsNullOrWhiteSpace(minSeverity))
        {
            var minRank = SeverityRank(minSeverity);
            query = query.Where(log => SeverityRank(log.Severity) >= minRank);
        }

        return Task.FromResult(query.ToArray());
    }

    public Task<AspireTraceSpan[]> GetTracesAsync(string? resourceName = null, bool errorsOnly = false)
    {
        var merged = State.Traces
            .Concat(AspireLiveTelemetry.SnapshotSpans())
            .GroupBy(static span => span.SpanId, StringComparer.Ordinal)
            .Select(static g => g.First())
            .Select(static span => span with { Error = AspireLiveTelemetry.Sanitize(span.Error) });

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            merged = merged.Where(span => KernelNames.Same(span.Resource, resourceName)
                || (KernelNames.IsKernel(resourceName)
                    && !span.Resource.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                    && (span.Resource.StartsWith("IntoChat", StringComparison.OrdinalIgnoreCase)
                        || span.Resource.StartsWith("DigitalBrain", StringComparison.OrdinalIgnoreCase)
                        || span.Resource.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))));
        }

        if (errorsOnly)
        {
            merged = merged.Where(span => !string.IsNullOrWhiteSpace(span.Error) || string.Equals(span.Status, "Error", StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(merged.TakeLast(200).ToArray());
    }

    public async Task RecordLogAsync(AspireLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sanitized = entry with
        {
            Message = AspireLiveTelemetry.Sanitize(entry.Message) ?? string.Empty,
            Exception = AspireLiveTelemetry.Sanitize(entry.Exception),
        };
        var nextLogs = State.StructuredLogs.Append(sanitized).TakeLast(200).ToArray();
        var next = State with { StructuredLogs = nextLogs };
        await Save(next, new AspireTelemetryChanged(next.StructuredLogs.Length, next.Traces.Length));
    }

    public async Task RecordTraceAsync(AspireTraceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        var sanitized = span with { Error = AspireLiveTelemetry.Sanitize(span.Error) };
        var nextTraces = State.Traces.Append(sanitized).TakeLast(200).ToArray();
        var next = State with { Traces = nextTraces };
        await Save(next, new AspireTelemetryChanged(next.StructuredLogs.Length, next.Traces.Length));
    }

    private static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "TRACE" or "DEBUG" => 1,
        "INFO" or "INFORMATION" => 2,
        "WARN" or "WARNING" => 3,
        "ERROR" => 4,
        "CRITICAL" or "FATAL" => 5,
        _ => 0,
    };
}
