using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DigitalBrain.Invocation;

/// <summary>
/// HTTP client for relaying sealed <see cref="BrainEnvelope"/> messages through a circle relay endpoint,
/// polling for incoming envelopes to dispatch through a local <see cref="BrainEnvelopeReceiver"/>,
/// and exchanging self-signed <see cref="FriendCodeBundle"/> payloads over <c>/circle/v1/friends/codes/*</c>.
/// </summary>
public sealed class HttpBrainRelayClient
{
    private const string SendPath = "/circle/v1/brains/send";
    private const string NextPath = "/circle/v1/brains/next";
    private const string ReplyPath = "/circle/v1/brains/reply";
    private const string WaitReplyPrefix = "/circle/v1/brains/reply/";
    private const string AckPrefix = "/circle/v1/brains/ack/";
    private const string LeaseIdHeader = "X-Brain-Lease-Id";
    private const string FriendCodesPath = "/circle/v1/friends/codes";
    private const string FriendCodesResolvePath = "/circle/v1/friends/codes/resolve";
    private const string FriendCodesPrefix = "/circle/v1/friends/codes/";

    private readonly HttpClient _http;
    private readonly string _basePrefix;
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask>? _authorize;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a relay client targeting <paramref name="relayBaseUri"/>.</summary>
    public HttpBrainRelayClient(
        HttpClient http,
        Uri relayBaseUri,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? authorize = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(relayBaseUri);

        _http = http;
        _basePrefix = relayBaseUri.AbsoluteUri.TrimEnd('/');
        _authorize = authorize;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Posts a one-way <paramref name="envelope"/> to the relay without waiting for a reply.
    /// Compatible with <see cref="SealedRelayBrainTransport"/>'s one-way post delegate.
    /// </summary>
    public async Task SendOneWayAsync(BrainEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrEmpty(envelope.MessageId);

        var oneWayEnvelope = envelope.OneWay ? envelope : envelope with { OneWay = true };
        await PostEnvelopeAsync(SendPath, oneWayEnvelope, ct).ConfigureAwait(false);
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
    /// Explicitly acknowledges a leased envelope via <c>POST /circle/v1/brains/ack/{messageId}</c>.
    /// </summary>
    public async Task AckAsync(string messageId, string? leaseId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + AckPrefix + Uri.EscapeDataString(messageId)));
        if (!string.IsNullOrWhiteSpace(leaseId))
        {
            request.Headers.TryAddWithoutValidation(LeaseIdHeader, leaseId);
        }

        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Polls the relay once for the next incoming envelope, delivers it to <paramref name="receiver"/>,
    /// and either posts the sealed reply back to the relay (implicit ack) or explicitly acks <see cref="BrainEnvelope.OneWay"/> envelopes.
    /// Returns <see langword="false"/> on <c>204 No Content</c>.
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
        string? leaseId = response.Headers.TryGetValues(LeaseIdHeader, out var leaseValues)
            ? leaseValues.FirstOrDefault()
            : null;
        var incoming = await ReadEnvelopeAsync(response, ct).ConfigureAwait(false);
        var reply = await receiver.DeliverAsync(incoming, ct).ConfigureAwait(false);
        if (incoming.OneWay)
        {
            await AckAsync(incoming.MessageId, leaseId, ct).ConfigureAwait(false);
        }
        else
        {
            await PostEnvelopeAsync(ReplyPath, reply, ct, leaseId).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Publishes a self-signed <see cref="FriendCodeBundle"/> to <c>POST /circle/v1/friends/codes</c>.
    /// Verifies the bundle self-signature locally before sending.
    /// </summary>
    public async Task PublishFriendCodeAsync(FriendCodeBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        FriendCodeBundleSigner.VerifyOrThrow(bundle, _timeProvider.GetUtcNow());

        var payload = JsonSerializer.SerializeToUtf8Bytes(bundle, RuntimeJsonContext.Default.FriendCodeBundle);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + FriendCodesPath))
        {
            Content = content,
        };
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Resolves a <see cref="FriendCodeBundle"/> via <c>POST /circle/v1/friends/codes/resolve</c> and verifies
    /// its self-signature before returning so the relay cannot MITM public keys.
    /// Returns <see langword="null"/> when the code hash is not found (<c>404 Not Found</c>).
    /// </summary>
    public async Task<FriendCodeBundle?> ResolveFriendCodeAsync(string codeHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        var body = JsonSerializer.SerializeToUtf8Bytes(new ResolveFriendCodeRequest(codeHash), RuntimeJsonContext.Default.ResolveFriendCodeRequest);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + FriendCodesResolvePath))
        {
            Content = content,
        };
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var resolved = await JsonSerializer.DeserializeAsync(stream, RuntimeJsonContext.Default.ResolveFriendCodeResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Relay response contained a null ResolveFriendCodeResponse.");

        var bundle = new FriendCodeBundle(
            CodeHash: codeHash,
            BrainId: resolved.BrainId,
            SigningPublicKey: resolved.SigningPublicKey,
            ExchangePublicKey: resolved.ExchangePublicKey,
            SelfSignature: resolved.SelfSignature,
            RelayOrigin: resolved.RelayOrigin,
            ExpiresAt: resolved.ExpiresAt);

        FriendCodeBundleSigner.VerifyOrThrow(bundle, _timeProvider.GetUtcNow());
        return bundle;
    }

    /// <summary>Revokes or consumes a published friend code via <c>DELETE /circle/v1/friends/codes/{codeHash}</c>.</summary>
    public async Task RevokeFriendCodeAsync(string codeHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_basePrefix + FriendCodesPrefix + Uri.EscapeDataString(codeHash)));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PostEnvelopeAsync(string path, BrainEnvelope envelope, CancellationToken ct, string? leaseId = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, RuntimeJsonContext.Default.BrainEnvelope);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + path))
        {
            Content = content,
        };
        if (!string.IsNullOrWhiteSpace(leaseId))
        {
            request.Headers.TryAddWithoutValidation(LeaseIdHeader, leaseId);
        }

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
