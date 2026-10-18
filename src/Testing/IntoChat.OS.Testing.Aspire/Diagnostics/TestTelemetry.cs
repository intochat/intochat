using System.Diagnostics;
using System.Text.Json;
using IntoChat.OS.ServiceDefaults;
using OpenTelemetry.Context.Propagation;

namespace IntoChat.OS.Testing.Aspire.Diagnostics;

/// <summary>A shared dashboard connection. Captures never clear the fixture's telemetry.</summary>
public sealed class TestTelemetry
{
    internal const int RetentionLimit = 1_000_000;
    private readonly DashboardTelemetryClient _dashboard;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TelemetryExportReceipt>>> _flush;

    internal TestTelemetry(DashboardTelemetryClient dashboard,
        Func<CancellationToken, Task<IReadOnlyList<TelemetryExportReceipt>>> flush)
    { _dashboard = dashboard; _flush = flush; }

    public TelemetryOperation BeginOperation(string name) => new(this, name);

    // Observation time is intentional here: this measures background rate, not export latency.
    public async Task<TelemetryCapture> ObserveAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        await ReadFlushedAsync(cancellationToken).ConfigureAwait(false);
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        return await CaptureAsync(start, DateTimeOffset.UtcNow, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TelemetryCapture> CompleteAsync(TelemetryOperation operation, CancellationToken cancellationToken)
    {
        var expected = operation.RequestParents;
        if (expected.Count == 0) { throw new InvalidOperationException("A telemetry operation must send at least one correlated request."); }
        DashboardTelemetryData spans = new(), logs = new();
        // Flush before querying: do not wait for the normal batch timer. Every request has
        // a distinct parent, so an earlier response cannot stand in for the last response.
        await WaitAsync(async ct =>
        {
            (spans, logs) = await ReadFlushedAsync(ct).ConfigureAwait(false);
            return RequestsCompleted(spans, operation.TraceId, expected);
        }, "all of the operation's completed HTTP server spans", cancellationToken).ConfigureAwait(false);
        return Capture(spans, logs, operation.StartedAt, DateTimeOffset.UtcNow, operation.TraceId);
    }

    internal static bool RequestsCompleted(DashboardTelemetryData spans, ActivityTraceId traceId, IReadOnlySet<string> expected)
    {
        var completed = Flatten(spans).Where(span => span.Data.Kind == DashboardSpanKind.Server
            && span.TraceId == traceId.ToHexString()).Select(span => span.ParentSpanId).ToHashSet(StringComparer.Ordinal);
        return expected.Count > 0 && expected.All(parent => completed.Contains(parent));
    }

    private async Task<TelemetryCapture> CaptureAsync(DateTimeOffset start, DateTimeOffset end,
        ActivityTraceId? trace, CancellationToken cancellationToken)
    {
        var (spans, logs) = await ReadFlushedAsync(cancellationToken).ConfigureAwait(false);
        return Capture(spans, logs, start, end, trace);
    }

    internal static TelemetryCapture Capture(DashboardTelemetryData spans, DashboardTelemetryData logs,
        DateTimeOffset start, DateTimeOffset end, ActivityTraceId? trace)
    {
        var from = UnixNanoseconds(start);
        var until = UnixNanoseconds(end);
        var evidence = Flatten(spans).Where(span => span.Scope != TelemetryExportBarrier.SourceName && span.Scope != "dotnet-cli").ToArray();
        var window = evidence.Where(span => span.Data.StartTimeUnixNano >= from && span.Data.StartTimeUnixNano <= until).ToArray();
        var privacy = evidence.Where(span => span.Data.StartTimeUnixNano <= until && span.Data.EndTimeUnixNano >= from).ToArray();
        var capturedLogs = FlattenLogs(logs).Where(log => log.Data.TimeUnixNano >= from
            && log.Data.TimeUnixNano <= until).ToArray();
        return new(window, capturedLogs, trace, privacy);
    }

    private async Task<(DashboardTelemetryData Spans, DashboardTelemetryData Logs)> ReadFlushedAsync(CancellationToken cancellationToken)
    {
        // Each child provider acknowledges its own export. A marker in both signals proves dashboard
        // ingestion too; observing an arbitrary span or a quiet interval cannot establish this.
        var receipts = await _flush(cancellationToken).ConfigureAwait(false);
        if (receipts.Count == 0) { throw new InvalidOperationException("No telemetry exporters were registered for this host."); }
        DashboardTelemetryData spans = new(), logs = new();
        await WaitAsync(async ct =>
        {
            spans = await _dashboard.GetSpansAsync(null, ct).ConfigureAwait(false);
            logs = await _dashboard.GetLogsAsync(null, ct).ConfigureAwait(false);
            ValidateComplete(spans, logs);
            return receipts.All(receipt => Flatten(spans).Any(span => HasMarker(span.Data.Attributes, receipt.Marker))
                && FlattenLogs(logs).Any(log => HasMarker(log.Data.Attributes, receipt.Marker)));
        }, "every resource's trace and log export receipts", cancellationToken).ConfigureAwait(false);
        // Discover resource identity from authenticated per-process receipts, not a name guess.
        // The dashboard also records AppHost probes and test-client instrumentation: these are
        // orchestration, not application work. All opted-in application emitters remain covered.
        var applicationResources = receipts.Select(receipt => ResourceIdentity(Flatten(spans)
            .Single(span => HasMarker(span.Data.Attributes, receipt.Marker)).Resource)).ToHashSet();
        var applicationLogResources = receipts.Select(receipt => ResourceIdentity(FlattenLogs(logs)
            .Single(log => HasMarker(log.Data.Attributes, receipt.Marker)).Resource)).ToHashSet();
        if (applicationResources.Any(resource => string.IsNullOrWhiteSpace(resource.Service))
            || applicationLogResources.Any(resource => string.IsNullOrWhiteSpace(resource.Service)))
        { throw new InvalidDataException("An exporter receipt has no service.name; application telemetry cannot be isolated safely."); }
        return (new DashboardTelemetryData
        {
            AdditionalFields = spans.AdditionalFields,
            ResourceSpans = spans.ResourceSpans.Where(resource => applicationResources.Contains(ResourceIdentity(resource.Resource))).ToArray(),
        }, new DashboardTelemetryData
        {
            AdditionalFields = logs.AdditionalFields,
            ResourceLogs = logs.ResourceLogs.Where(resource => applicationLogResources.Contains(ResourceIdentity(resource.Resource))).ToArray(),
        });
    }

    private static (string? Service, string? Instance) ResourceIdentity(DashboardResource? resource)
        => (resource?.Attributes.FirstOrDefault(attribute => attribute.Key == "service.name")?.Value?.StringValue,
            resource?.Attributes.FirstOrDefault(attribute => attribute.Key == "service.instance.id")?.Value?.StringValue);

    internal static void ValidateComplete(DashboardTelemetryData spans, DashboardTelemetryData logs)
    {
        if (Flatten(spans).Count() >= RetentionLimit || FlattenLogs(logs).Count() >= RetentionLimit)
        { throw new InvalidDataException("Dashboard retention capacity reached; capture completeness cannot be established."); }
        if (spans.ResourceSpans.Any(resource => resource.Resource?.DroppedAttributesCount > 0
            || resource.ScopeSpans.Any(scope => scope.Scope?.DroppedAttributesCount > 0 || scope.Spans.Any(span =>
                span.DroppedAttributesCount > 0 || span.DroppedEventsCount > 0 || span.DroppedLinksCount > 0
                || span.Events.Any(item => item.DroppedAttributesCount > 0) || span.Links.Any(item => item.DroppedAttributesCount > 0))))
            || logs.ResourceLogs.Any(resource => resource.Resource?.DroppedAttributesCount > 0
                || resource.ScopeLogs.Any(scope => scope.Scope?.DroppedAttributesCount > 0 || scope.LogRecords.Any(log => log.DroppedAttributesCount > 0))))
        { throw new InvalidDataException("Telemetry reports dropped fields; absence assertions require complete payloads."); }
    }

    private static bool HasMarker(IEnumerable<DashboardKeyValue> attributes, Guid marker)
        => attributes.Any(attribute => attribute.Key is "TelemetryBarrierMarker" or TelemetryExportBarrier.MarkerAttribute
            && string.Equals(attribute.Value?.StringValue, marker.ToString(), StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<TelemetrySpan> Flatten(DashboardTelemetryData data)
        => data.ResourceSpans.SelectMany(resource => resource.ScopeSpans.SelectMany(scope =>
            scope.Spans.Select(span => new TelemetrySpan(resource.Resource, scope.Scope, span,
                TelemetryText.Of(new
                {
                    data.AdditionalFields,
                    ResourceSchema = resource.SchemaUrl,
                    ResourceFields = resource.AdditionalFields,
                    ScopeSchema = scope.SchemaUrl,
                    ScopeFields = scope.AdditionalFields
                })))));

    internal static IEnumerable<TelemetryLog> FlattenLogs(DashboardTelemetryData data)
        => data.ResourceLogs.SelectMany(resource => resource.ScopeLogs.SelectMany(scope =>
            scope.LogRecords.Select(log => new TelemetryLog(resource.Resource, scope.Scope, log,
                TelemetryText.Of(new
                {
                    data.AdditionalFields,
                    ResourceSchema = resource.SchemaUrl,
                    ResourceFields = resource.AdditionalFields,
                    ScopeSchema = scope.SchemaUrl,
                    ScopeFields = scope.AdditionalFields
                })))));

    private static ulong UnixNanoseconds(DateTimeOffset timestamp)
        => checked((ulong)(timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);

    private static async Task WaitAsync(Func<CancellationToken, Task<bool>> condition, string description, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (!await condition(deadline.Token).ConfigureAwait(false))
            { await timer.WaitForNextTickAsync(deadline.Token).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Timed out awaiting {description}."); }
    }
}

/// <summary>One explicit W3C context; requests never mutate a shared HttpClient's default headers.</summary>
public sealed class TelemetryOperation : IDisposable
{
    private static readonly TraceContextPropagator TraceContext = new();
    private readonly TestTelemetry _telemetry;
    private readonly Activity _activity;
    private readonly HashSet<string> _requestParents = new(StringComparer.Ordinal);
    private bool _completed;

    internal TelemetryOperation(TestTelemetry telemetry, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _telemetry = telemetry;
        StartedAt = DateTimeOffset.UtcNow;
        _activity = new Activity(name).SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();
    }

    public ActivityTraceId TraceId => _activity.TraceId;
    public ActivitySpanId SpanId => _activity.SpanId;
    public DateTimeOffset StartedAt { get; }
    public string TraceParent => _activity.Id!;
    internal IReadOnlySet<string> RequestParents { get { lock (_requestParents) { return _requestParents.ToHashSet(StringComparer.Ordinal); } } }

    public HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var request = new HttpRequestMessage(method, path) { Content = content };
        var parent = ActivitySpanId.CreateRandom();
        lock (_requestParents) { _requestParents.Add(parent.ToHexString()); }
        TraceContext.Inject(new PropagationContext(new ActivityContext(TraceId, parent, ActivityTraceFlags.Recorded), default),
            request, static (carrier, name, value) => carrier.Headers.Add(name, value));
        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        // The AppHost installs process-wide HttpClient instrumentation in the test process.
        // Suppress that observer so it cannot replace our explicit per-request parent context.
        var previous = Activity.Current;
        Activity.Current = null;
        using var suppressed = OpenTelemetry.SuppressInstrumentationScope.Begin();
        try { return await client.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { Activity.Current = previous; }
    }

    /// <summary>Call only after the tested business operation and its required projections have completed.</summary>
    public Task<TelemetryCapture> CompleteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Dispose();
        return _telemetry.CompleteAsync(this, cancellationToken);
    }

    public void Dispose()
    {
        if (_completed) { return; }
        _completed = true;
        _activity.Dispose();
    }
}

public sealed record TelemetryCapture(IReadOnlyList<TelemetrySpan> AllSpans, IReadOnlyList<TelemetryLog> Logs, ActivityTraceId? TraceId,
    IReadOnlyList<TelemetrySpan>? OverlappingSpans = null)
{
    public IReadOnlyList<TelemetrySpan> PrivacySpans => OverlappingSpans ?? AllSpans;
    // Whole-window evidence remains available: privacy and total-work budgets must not hide
    // uncorrelated projections, while instrumentation assertions require the actual operation trace.
    public IReadOnlyList<TelemetrySpan> Spans => TraceId is { } trace
        ? AllSpans.Where(span => span.Data.TraceId == trace.ToHexString()).ToArray() : AllSpans;
}

public sealed record TelemetrySpan(DashboardResource? Resource, DashboardInstrumentationScope? Instrumentation, DashboardSpan Data, string MetadataText = "")
{
    public string Name => Data.Name;
    public string Scope => Instrumentation?.Name ?? string.Empty;
    public string TraceId => Data.TraceId;
    public string SpanId => Data.SpanId;
    public string? ParentSpanId => Data.ParentSpanId;
    public string Text => MetadataText + "\n" + TelemetryText.Of(new { Resource, Instrumentation, Data });
    public bool IsGenAi => Scope.StartsWith("IntoChat.OS.AI", StringComparison.Ordinal)
        || Scope.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal)
        || Scope.StartsWith("Experimental.Microsoft.Extensions.AI", StringComparison.Ordinal);
}

public sealed record TelemetryLog(DashboardResource? Resource, DashboardInstrumentationScope? Instrumentation, DashboardLogRecord Data, string MetadataText = "")
{
    public string Text => MetadataText + "\n" + TelemetryText.Of(new { Resource, Instrumentation, Data });
}

internal static class TelemetryText
{
    public static string Of<T>(T value) => string.Join("\n", Strings(JsonSerializer.SerializeToElement(value, DashboardTelemetryClient.JsonOptions)));

    private static IEnumerable<string> Strings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) { yield return element.GetString()!; }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var text in Strings(property.Value)) { yield return text; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            { foreach (var text in Strings(child)) { yield return text; } }
        }
        else { yield return element.GetRawText(); }
    }
}
