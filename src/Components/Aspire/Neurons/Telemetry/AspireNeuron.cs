using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain.Confirmation;
using DigitalBrain.Log;
using DigitalBrain.State;
using Microsoft.Extensions.Configuration;

namespace IntoChat.Aspire;

// RequestedAt is absent from entries stored before it existed: it reads as default (DateTimeOffset.MinValue), which is stale.
internal sealed record AspirePendingRestart(string PlanId, string Resource, DateTimeOffset RequestedAt = default);

[ExportableState]
internal sealed record AspireTelemetryState(
    AspireResourceView[] Resources,
    AspireLogEntry[] StructuredLogs,
    AspireTraceSpan[] Traces)
{
    public AspireTelemetryState() : this([], [], [])
    {
    }

    // Restarts waiting for a person's answer. Null (not written) when none wait, so stored state without it reads and writes the same.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AspirePendingRestart[]? PendingRestarts { get; init; }
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
internal sealed partial class AspireNeuron : DigitalBrain.Neurons.Neuron<AspireTelemetryState>, IAspire
{
    // Optional: a runtime that hosts no confirmations still activates this neuron; Restart then declines every non-person caller.
    [DigitalBrain.Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)]
    private partial IConfirmationRequests? Confirmations { get; }

    private readonly IConfiguration? _configuration;
    private readonly IAspireTools? _mcp;
    private readonly string? _self;


    public AspireNeuron(IConfiguration? configuration = null, IAspireTools? mcp = null, AspireInstance? self = null)
    {
        _configuration = configuration;
        _mcp = mcp;
        _self = self?.Name;
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

    // A direct person restarts at once. Anyone else gets "pending" and a plan id: the request is stored, the person is asked without
    // holding this neuron's turn, and OnRestartAnswered runs it once a person approves. Refusals are returned values or signals.
    public async Task<AspireRestartResult> Restart(string resourceName)
    {
        var (name, refused) = await Check(resourceName);
        if (refused is not null) { return refused; }
        await DropStale();
        if (IsDirectPerson(Context.Caller)) { return await Run(name, Pending(), null); }

        if (Pending().FirstOrDefault(item => item.Resource == name) is { } waiting) { return new(name, false, "pending", Confirmation: waiting.PlanId); }
        if (Pending().Length >= MaxPending) { return new(name, false, "declined", Message: $"{MaxPending} restarts already wait for the person's answer."); }
        var plan = new ConfirmationPlan($"{RestartKind}:{name}:{Guid.NewGuid():N}", ConfirmationAction.Other, false,
            $"Restart {name}?", $"Restart the resource {name} of the running app.", "Restart", "Keep running", name)
        { RequiresPerson = true, Kind = RestartKind, Subject = name };
        // The id is ours, so the question is asked first and the entry stored only once it exists: no entry ever waits on a
        // question that was never placed. Its answer cannot overtake the save, because it arrives as a signal this neuron handles
        // in a later turn. A failed save after a placed question leaves a question whose answer finds no entry and runs nothing.
        ConfirmationResult? immediate;
#pragma warning disable CA1031 // a refused question is a returned value and a signal, never a throw
        try { immediate = await (Confirmations ?? throw new InvalidOperationException("No confirmations are hosted in this runtime.")).Request(plan); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = AspireLiveTelemetry.Sanitize(error.Message);
            await Save(State, new AspireResourceRestartRefused(name, "declined", message));
            return new(name, false, "declined", Message: message);
        }
#pragma warning restore CA1031
        if (immediate is not null)
        {
            // Answered without asking (policy Never, or already known): only a person's own yes runs a restart, so this is a no.
            await Save(State, new AspireResourceRestartRefused(name, "declined"));
            return new(name, false, "declined", Confirmation: plan.Id);
        }
        await Save(State with { PendingRestarts = [.. Pending(), new AspirePendingRestart(plan.Id, name, Context.Time.GetUtcNow())] },
            new AspireResourceRestartRequested(name, plan.Id));
        return new(name, false, "pending", Confirmation: plan.Id);
    }

    // The person's answer to a pending restart. Reads the authoritative outcome; only a person's own approval runs it, after the name
    // is checked again against the live list. Every answer removes the request. Never calls back into this neuron's contract.
    [DigitalBrain.Neurons.On]
    public async Task OnRestartAnswered(ConfirmationResolved resolved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ct.ThrowIfCancellationRequested();
        await DropStale();
        if (Pending().FirstOrDefault(item => item.PlanId == resolved.PlanId) is not { } pending) { return; }
        var outcome = Confirmations is { } confirmations ? await confirmations.Outcome(resolved.PlanId) : null;
        var rest = Without(pending.PlanId);
        // A null outcome means no record behind a resolved plan: nothing can ever answer it, so it ends as declined like any other no.
        if (outcome is not { Approved: true, Skipped: false, Approver.IsHuman: true })
        {
            await Save(State with { PendingRestarts = rest }, new AspireResourceRestartRefused(pending.Resource, "declined"));
            return;
        }
        var (name, refused) = await Check(pending.Resource);
        if (refused is not null)
        {
            await Save(State with { PendingRestarts = rest }, new AspireResourceRestartRefused(pending.Resource, refused.Reason!, refused.Message));
            return;
        }
        await Run(name, rest ?? [], pending.PlanId);
    }

    protected override Task OnActivate(CancellationToken cancellationToken)
        => Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));

    // Availability first, then the name, then the kernel itself. The name is the exact live name, or the display name of exactly one
    // live resource (the dashboard drops the instance suffix list_resources keeps); several sharing it are refused as unknown, unless
    // one of them is the kernel. The name returned is always the live list's exact name.
    private async Task<(string Name, AspireRestartResult? Refused)> Check(string? resourceName)
    {
        if (_mcp is not { Available: true }) { return ("", new(resourceName ?? "", false, "unavailable")); }
        var live = await _mcp.AllResources(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(resourceName)) { return ("", new(resourceName ?? "", false, "unknown", [.. live.Select(view => view.Name).Take(MaxKnown)])); }
        AspireResourceView[] matches = live.FirstOrDefault(view => string.Equals(view.Name, resourceName, StringComparison.Ordinal)) is { } exact
            ? [exact]
            : [.. live.Where(view => DisplayNames(view).Contains(resourceName, StringComparer.Ordinal))];
        if (matches.FirstOrDefault(IsSelf) is { } self) { return ("", new(self.Name, false, "self")); }
        return matches switch
        {
            [var match] => (match.Name, null),
            [] => ("", new(resourceName, false, "unknown", [.. live.Select(view => view.Name).Take(MaxKnown)])),
            _ => ("", new(resourceName, false, "unknown", [.. matches.Select(view => view.Name).Concat(live.Select(view => view.Name).Except(matches.Select(view => view.Name))).Take(MaxKnown)])),
        };
    }

    private static string[] DisplayNames(AspireResourceView view)
        => [.. new[] { view.DisplayName, AspireMcp.DisplayNameOf(view.Name), view.DisplayName is null ? null : AspireMcp.DisplayNameOf(view.DisplayName) }.OfType<string>().Distinct()];

    // Fails closed: the kernel, its front, this process or a rebuilder of any of them under its exact name or any display name.
    private bool IsSelf(AspireResourceView view)
        => DisplayNames(view).Append(view.Name).Any(name => KernelNames.IsSelf(name, _self)
            || (name.EndsWith(Rebuilder, StringComparison.Ordinal) && name.Length > Rebuilder.Length
                && KernelNames.IsSelf(AspireMcp.DisplayNameOf(name[..^Rebuilder.Length]), _self)));

    private const string Rebuilder = "-rebuilder";

    // Runs the command and saves the state (any answered request removed) with the outcome signal.
    private async Task<AspireRestartResult> Run(string name, AspirePendingRestart[] pending, string? planId)
    {
        var next = State with { PendingRestarts = pending.Length == 0 ? null : pending };
#pragma warning disable CA1031 // a failed command is a returned value and a signal, never a throw
        try { await _mcp!.Command(name, AspireMcp.RestartCommand, CancellationToken.None); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<AspireNeuron>().LogSwallowed(error, $"restarting the Aspire resource {name}");
            var message = AspireLiveTelemetry.Sanitize(error.Message);
            await Save(next, new AspireResourceRestartRefused(name, "failed", message));
            return new(name, false, "failed", Message: message, Confirmation: planId);
        }
#pragma warning restore CA1031
        await Save(next, new AspireResourceRestarted(name, Context.Time.GetUtcNow()));
        return new(name, true, Confirmation: planId);
    }

    private AspirePendingRestart[] Pending() => State.PendingRestarts ?? [];

    // Entries whose answer was lost: a question expires after ConfirmationRules.DefaultExpiry and then publishes its answer, so an
    // entry three times older has missed it. Each is dropped as declined/"expired" and its resource may be asked again.
    private static readonly TimeSpan StaleAfter = 3 * ConfirmationRules.DefaultExpiry;

    private async Task DropStale()
    {
        var cutoff = Context.Time.GetUtcNow() - StaleAfter;
        var stale = Pending().Where(item => item.RequestedAt < cutoff).ToArray();
        if (stale.Length == 0) { return; }
        var kept = Pending().Except(stale).ToArray();
        await Save(State with { PendingRestarts = kept.Length == 0 ? null : kept },
            [.. stale.Select(item => (DigitalBrain.Signal)new AspireResourceRestartRefused(item.Resource, "declined", "expired"))]);
    }

    private AspirePendingRestart[]? Without(string planId)
        => Pending().Where(item => item.PlanId != planId).ToArray() is { Length: > 0 } rest ? rest : null;

    private const int MaxKnown = 64;
    private const int MaxPending = 16;

    // The same rule Answerer.Of uses for person authority: a stamped person, direct, outside any conversation.
    private static bool IsDirectPerson(DigitalBrain.Access.Caller? caller)
        => caller is { Direct: true, Conversation: null, Actor: DigitalBrain.Access.Actor.Person };

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
