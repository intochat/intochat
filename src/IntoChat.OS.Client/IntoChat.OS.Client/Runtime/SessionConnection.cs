using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client.Runtime;

// One per sandbox session, shared by every behavior in it: invokes and reports over HTTP, and one
// stream from the brain that carries assignments, token rotation and every subscription's batches.
// A dropped stream is reopened with backoff; the brain resends what is unacknowledged, and a signal
// already queued for a subscription is never queued twice (its number is the dedup key). Readers that
// share a subscription id each get every batch (SharedSubscription).
public sealed class SessionConnection : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<(string Kind, string Data)> _control = Channel.CreateUnbounded<(string, string)>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, SharedSubscription> _subscriptions = new(StringComparer.Ordinal);
    private volatile string _token;
    private Task _pump = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;
    private readonly Channel<PendingAcknowledgement> _acknowledgements = Channel.CreateUnbounded<PendingAcknowledgement>(new() { SingleReader = true });
    private Task _acknowledger = Task.CompletedTask;
    private readonly TimeProvider _timeProvider;

    private SessionConnection(Uri edge, string token, HttpMessageHandler? handler, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = edge;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _token = token;
        Brain = edge.Segments is [.., "brains/", var brain] ? Uri.UnescapeDataString(brain.TrimEnd('/')) : "";
    }

    public string Brain { get; }

    public CancellationToken Stopping => _lifetime.Token;

    public IAsyncEnumerable<(string Kind, string Data)> Control => _control.Reader.ReadAllAsync();

    public static Task<SessionConnection> OpenAsync(Uri edge, string token, string fingerprint, CancellationToken cancellationToken)
        => OpenAsync(edge, token, fingerprint, handler: null, cancellationToken);

    // The brain refuses a host built from other contracts before anything runs.
    public static Task<SessionConnection> OpenAsync(Uri edge, string token, string fingerprint, HttpMessageHandler? handler, CancellationToken cancellationToken)
        => OpenAsync(edge, token, fingerprint, handler, TimeProvider.System, cancellationToken);

    internal static async Task<SessionConnection> OpenAsync(Uri edge, string token, string fingerprint, HttpMessageHandler? handler, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var connection = new SessionConnection(new Uri(edge.ToString().TrimEnd('/') + "/"), token, handler, timeProvider);
        try
        {
            await connection.ReportAsync(new RuntimeReport(Fingerprint: fingerprint), "connect", cancellationToken).ConfigureAwait(false);
            connection._pump = connection.PumpAsync(connection._lifetime.Token);
            connection._heartbeat = connection.HeartbeatAsync(connection._lifetime.Token);
            connection._acknowledger = connection.AcknowledgePendingAsync(connection._lifetime.Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonElement?> InvokeAsync(RuntimeInvocation invocation, CancellationToken cancellationToken)
        => InvokeAsync(invocation, traceParent: null, cancellationToken);

    // traceParent joins the call to the trace of the signal the behavior is reacting to.
    public async Task<JsonElement?> InvokeAsync(RuntimeInvocation invocation, string? traceParent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        // The call becomes a span of the signal's trace, so HTTP instrumentation in the host parents to it too.
        using var activity = traceParent is null ? null : new System.Diagnostics.Activity("IntoChat.OS.Behavior.Invoke").SetParentId(traceParent).Start();
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Invoke, invocation, HttpCompletionOption.ResponseContentRead, cancellationToken,
            System.Diagnostics.Activity.Current?.Id).ConfigureAwait(false);
        await EnsureSuccessAsync(response, invocation.Contract + "." + invocation.Method, cancellationToken).ConfigureAwait(false);
        return response.Content.Headers.ContentLength == 0 || response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<JsonElement>(RuntimeProtocol.Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SubscribeAsync(SubscribeReport subscribe, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Report, new RuntimeReport(Subscribe: subscribe), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "subscribe to " + subscribe.Signal, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(RuntimeProtocol.Json, cancellationToken).ConfigureAwait(false);
        return body.GetProperty("subscription").GetString() ?? throw new InvalidOperationException("The brain answered a subscribe without a subscription id.");
    }

    public async Task AcknowledgeAsync(string subscription, long watermark, CancellationToken cancellationToken, long cpuMicroseconds = 0, long wallMicroseconds = 0, int batches = 1, bool skipped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingAcknowledgement(new(subscription, watermark, cpuMicroseconds, wallMicroseconds, batches, skipped), System.Diagnostics.Activity.Current?.Id);
        if (!_acknowledgements.Writer.TryWrite(pending)) { throw new ObjectDisposedException(nameof(SessionConnection)); }
        // Cancelling a caller's wait must not cancel a report shared with other subscriptions.
        await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AcknowledgePendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _acknowledgements.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var pending = new List<PendingAcknowledgement>();
                while (pending.Count < RuntimeProtocol.MaximumAcknowledgements && _acknowledgements.Reader.TryRead(out var item))
                { pending.Add(item); }
                // Keep unrelated parent contexts separate. Only requests already waiting are grouped;
                // there is no timer, and the first report is sent as soon as the sender is idle.
                foreach (var group in pending.GroupBy(item => item.Parent, StringComparer.Ordinal))
                {
                    using var activity = group.Key is null ? null : new System.Diagnostics.Activity("IntoChat.OS.Behavior.Acknowledge").SetParentId(group.Key).Start();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
                    using var reportLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                    try
                    {
                        // One report per subscription covers every batch queued for it: the watermark is the highest,
                        // the time and batches are the sum of what each batch took.
                        var reports = group.GroupBy(item => item.Report.Subscription, StringComparer.Ordinal)
                            .Select(items => new AcknowledgeReport(items.Key, items.Max(item => item.Report.Watermark),
                                items.Sum(item => item.Report.CpuMicroseconds), items.Sum(item => item.Report.WallMicroseconds),
                                items.Sum(item => item.Report.Batches), items.All(item => item.Report.Skipped))).ToArray();
                        await ReportAsync(new RuntimeReport(Acknowledgements: reports), "acknowledge", reportLifetime.Token).ConfigureAwait(false);
                        foreach (var item in group) { item.Completion.TrySetResult(); }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    { foreach (var item in group) { item.Completion.TrySetCanceled(cancellationToken); } }
                    catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
                    { foreach (var item in group) { item.Completion.TrySetException(new TimeoutException("The acknowledgement report timed out.", error)); } }
                    catch (Exception error)
                    { foreach (var item in group) { item.Completion.TrySetException(error); } }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            while (_acknowledgements.Reader.TryRead(out var pending)) { pending.Completion.TrySetCanceled(cancellationToken); }
        }
    }

    private sealed record PendingAcknowledgement(AcknowledgeReport Report, string? Parent)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task ReportStatusAsync(BehaviorStatusReport status, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Report, new RuntimeReport(Status: status),
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (status.Status == "Initialized" && ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
        { throw new HttpRequestException("Initialization report temporarily unavailable.", null, response.StatusCode); }
        await EnsureSuccessAsync(response, "report status", cancellationToken).ConfigureAwait(false);
    }

    public Task ReportLogsAsync(string request, string text, CancellationToken cancellationToken)
        => ReportAsync(new RuntimeReport(Logs: new(request, text)), "report logs", cancellationToken);

    // A reader joins once the brain has registered the subscription, so a batch pushed meanwhile is kept for it.
    internal SharedSubscription.Reader Join(string subscription) => Subscription(subscription).Join();

    private SharedSubscription Subscription(string id) => _subscriptions.GetOrAdd(id, static (id, session) => new(session, id), this);

    public bool TryUpdateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) { return false; }
        _token = token;
        return true;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            // A dropped stream (any failure) settles the task; the backoff below reopens it, and cancellation ends the loop there.
            await Task.WhenAny(ReadStreamAsync()).ConfigureAwait(false);
            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
        }

        async Task ReadStreamAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, RuntimeProtocol.Stream, null, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "open the session stream", cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                backoff = TimeSpan.FromSeconds(1);
                Dispatch(item);
            }
        }
    }

    private void Dispatch(SseItem<string> item)
    {
        switch (item.EventType)
        {
            case SignalBatchEvent.Kind:
                var batch = JsonSerializer.Deserialize<SignalBatchEvent>(item.Data, RuntimeProtocol.Json)!;
                Subscription(batch.Subscription).Arrive(batch);
                return;
            case TokenEvent.Kind:
                TryUpdateToken(JsonSerializer.Deserialize<TokenEvent>(item.Data, RuntimeProtocol.Json)!.Token);
                return;
            default:
                _control.Writer.TryWrite((item.EventType, item.Data));
                return;
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await ReportAsync(new RuntimeReport(Heartbeat: true), "heartbeat", cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { continue; } // the brain's reminder relaunches a silent session
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReportAsync(RuntimeReport report, string operation, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Report, report, HttpCompletionOption.ResponseContentRead, cancellationToken,
            reportKind: report.Heartbeat ? RuntimeProtocol.HeartbeatReport : null).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, HttpCompletionOption completion, CancellationToken cancellationToken, string? traceParent = null, string? reportKind = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (traceParent is not null) { request.Headers.TryAddWithoutValidation("traceparent", traceParent); }
        if (reportKind is not null) { request.Options.Set(RuntimeProtocol.ReportKind, reportKind); }
        return _http.SendAsync(request, completion, cancellationToken);
    }

    // A body that is not the edge's conflict shape is still a refusal, not a parse failure.
    private static RevisionConflictException? TryReadConflict(string detail)
    {
        try
        {
            using var body = JsonDocument.Parse(detail);
            return body.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && root.TryGetProperty("currentRevision", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var current)
                ? new RevisionConflictException(message.GetString()!, current)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) { return; }
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues(RuntimeProtocol.RefusalHeader, out var codes))
        {
            var code = codes.Single();
            if (code == RevisionConflictException.Code && TryReadConflict(detail) is { } conflict) { throw conflict; }
            throw new ScriptRefusalException(detail, code);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new UnauthorizedAccessException(detail); }
        throw new InvalidOperationException($"The brain refused {operation} ({(int)response.StatusCode}): {detail}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) { return; }
        _acknowledgements.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_pump, _heartbeat, _acknowledger).ConfigureAwait(false);
        foreach (var subscription in _subscriptions.Values) { subscription.Complete(); }
        _control.Writer.TryComplete();
        _http.Dispose();
        _lifetime.Dispose();
    }
}
