using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Client.Runtime;

// One per sandbox session, shared by every behavior in it: invokes and reports over HTTP, and one
// stream from the brain that carries assignments, token rotation and every subscription's batches.
// A dropped stream is reopened with backoff; the brain resends what is unacknowledged, and a signal
// already queued for a subscription is never queued twice (its number is the dedup key).
public sealed class SessionConnection : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<(string Kind, string Data)> _control = Channel.CreateUnbounded<(string, string)>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, Channel<SignalBatchEvent>> _batches = new(StringComparer.Ordinal);
    // Per subscription, the highest signal number handed to a reader; anything at or below it is a resend.
    private readonly ConcurrentDictionary<string, long> _queued = new(StringComparer.Ordinal);
    private volatile string _token;
    private Task _pump = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;

    private SessionConnection(Uri edge, string token, HttpMessageHandler? handler)
    {
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
    public static async Task<SessionConnection> OpenAsync(Uri edge, string token, string fingerprint, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var connection = new SessionConnection(new Uri(edge.ToString().TrimEnd('/') + "/"), token, handler);
        try
        {
            await connection.ReportAsync(new RuntimeReport(Fingerprint: fingerprint), "connect", cancellationToken).ConfigureAwait(false);
            connection._pump = connection.PumpAsync(connection._lifetime.Token);
            connection._heartbeat = connection.HeartbeatAsync(connection._lifetime.Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement?> InvokeAsync(RuntimeInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Invoke, invocation, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
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

    public Task AcknowledgeAsync(string subscription, long watermark, CancellationToken cancellationToken)
        => ReportAsync(new RuntimeReport(Acknowledge: new(subscription, watermark)), "acknowledge", cancellationToken);

    public Task ReportStatusAsync(BehaviorStatusReport status, CancellationToken cancellationToken)
        => ReportAsync(new RuntimeReport(Status: status), "report status", cancellationToken);

    public Task ReportLogsAsync(string request, string text, CancellationToken cancellationToken)
        => ReportAsync(new RuntimeReport(Logs: new(request, text)), "report logs", cancellationToken);

    public ChannelReader<SignalBatchEvent> Batches(string subscription)
        => _batches.GetOrAdd(subscription, static _ => Channel.CreateUnbounded<SignalBatchEvent>(new() { SingleReader = true })).Reader;

    // A reader that stops leaves its signals unacknowledged at the brain; the next reader is resent them.
    public void Release(string subscription, long acknowledged)
    {
        if (_batches.TryRemove(subscription, out var channel)) { channel.Writer.TryComplete(); }
        _queued[subscription] = acknowledged;
    }

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
            try
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* the stream dropped; reopen it */ }
            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
        }
    }

    private void Dispatch(SseItem<string> item)
    {
        switch (item.EventType)
        {
            case SignalBatchEvent.Kind:
                var batch = JsonSerializer.Deserialize<SignalBatchEvent>(item.Data, RuntimeProtocol.Json)!;
                var first = batch.Watermark - batch.Signals.Length + 1;
                var queued = _queued.GetOrAdd(batch.Subscription, first - 1);
                var fresh = batch.Signals.Skip((int)Math.Clamp(queued - first + 1, 0, batch.Signals.Length)).ToArray();
                if (fresh.Length == 0) { return; }
                _queued[batch.Subscription] = batch.Watermark;
                Batches(batch.Subscription);
                _batches[batch.Subscription].Writer.TryWrite(batch with { Signals = fresh });
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
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { /* the brain's reminder relaunches a silent session */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReportAsync(RuntimeReport report, string operation, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, RuntimeProtocol.Report, report, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return _http.SendAsync(request, completion, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) { return; }
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues(RuntimeProtocol.RefusalHeader, out var codes))
        { throw new ScriptRefusalException(detail, codes.Single()); }
        throw new InvalidOperationException($"The brain refused {operation} ({(int)response.StatusCode}): {detail}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) { return; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_pump, _heartbeat).ConfigureAwait(false);
        foreach (var channel in _batches.Values) { channel.Writer.TryComplete(); }
        _control.Writer.TryComplete();
        _http.Dispose();
        _lifetime.Dispose();
    }
}
