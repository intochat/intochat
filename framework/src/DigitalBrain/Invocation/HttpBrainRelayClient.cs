using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DigitalBrain.Invocation;

/// <summary>
/// HTTP client for relaying sealed <see cref="BrainEnvelope"/> messages through a circle relay endpoint
/// and polling for incoming envelopes to dispatch through a local <see cref="BrainEnvelopeReceiver"/>.
/// </summary>
public sealed class HttpBrainRelayClient
{
    private const string SendPath = "/circle/v1/brains/send";
    private const string NextPath = "/circle/v1/brains/next";
    private const string ReplyPath = "/circle/v1/brains/reply";
    private const string WaitReplyPrefix = "/circle/v1/brains/reply/";

    private readonly HttpClient _http;
    private readonly string _basePrefix;
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask>? _authorize;

    /// <summary>Creates a relay client targeting <paramref name="relayBaseUri"/>.</summary>
    public HttpBrainRelayClient(
        HttpClient http,
        Uri relayBaseUri,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? authorize = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(relayBaseUri);

        _http = http;
        _basePrefix = relayBaseUri.AbsoluteUri.TrimEnd('/');
        _authorize = authorize;
    }

    /// <summary>
    /// Posts <paramref name="envelope"/> to the relay and long-polls until the matching reply envelope arrives.
    /// Compatible with <see cref="SealedRelayBrainTransport"/>'s relay delegate.
    /// </summary>
    public async Task<BrainEnvelope> SendAndWaitReplyAsync(BrainEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrEmpty(envelope.MessageId);

        await PostEnvelopeAsync(SendPath, envelope, ct).ConfigureAwait(false);

        var replyUri = new Uri(_basePrefix + WaitReplyPrefix + Uri.EscapeDataString(envelope.MessageId));
        while (!ct.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, replyUri);
            await AuthorizeAsync(request, ct).ConfigureAwait(false);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await ReadEnvelopeAsync(response, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Polls the relay once for the next incoming envelope, delivers it to <paramref name="receiver"/>,
    /// and posts the resulting sealed reply back to the relay. Returns <see langword="false"/> on <c>204 No Content</c>.
    /// </summary>
    public async Task<bool> PollOnceAsync(BrainEnvelopeReceiver receiver, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_basePrefix + NextPath));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        var incoming = await ReadEnvelopeAsync(response, ct).ConfigureAwait(false);
        var reply = await receiver.DeliverAsync(incoming, ct).ConfigureAwait(false);
        await PostEnvelopeAsync(ReplyPath, reply, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Continuously polls the relay and dispatches incoming envelopes to <paramref name="receiver"/>
    /// until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunPollLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await PollOnceAsync(receiver, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean exit on cancellation.
        }
    }

    private async Task PostEnvelopeAsync(string path, BrainEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, RuntimeJsonContext.Default.BrainEnvelope);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + path))
        {
            Content = content,
        };
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_authorize is not null)
        {
            await _authorize(request, ct).ConfigureAwait(false);
        }
    }

    private static async Task<BrainEnvelope> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, RuntimeJsonContext.Default.BrainEnvelope, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Relay response contained a null BrainEnvelope.");
    }
}
