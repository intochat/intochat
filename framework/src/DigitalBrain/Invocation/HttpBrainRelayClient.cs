using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace DigitalBrain.Invocation;

/// <summary>
/// HTTP and WebSocket client for relaying sealed <see cref="BrainEnvelope"/> messages through a circle relay endpoint,
/// streaming incoming envelopes over <c>GET /circle/v1/brains/stream</c> (with HTTP polling fallback),
/// exchanging self-signed <see cref="FriendCodeBundle"/> payloads over <c>/circle/v1/friends/codes/*</c>,
/// and transferring opaque E2EE media blobs over <c>/circle/v1/brains/blobs</c>.
/// </summary>
public sealed class HttpBrainRelayClient : IBrainRelayClient
{
    private const string SendPath = "/circle/v1/brains/send";
    private const string NextPath = "/circle/v1/brains/next";
    private const string ReplyPath = "/circle/v1/brains/reply";
    private const string WaitReplyPrefix = "/circle/v1/brains/reply/";
    private const string AckPrefix = "/circle/v1/brains/ack/";
    private const string StreamPath = "/circle/v1/brains/stream";
    private const string LeaseIdHeader = "X-Brain-Lease-Id";
    private const string FriendCodesPath = "/circle/v1/friends/codes";
    private const string FriendCodesResolvePath = "/circle/v1/friends/codes/resolve";
    private const string FriendCodesPrefix = "/circle/v1/friends/codes/";
    private const string BlobsPath = "/circle/v1/brains/blobs";
    private const string BlobsPrefix = "/circle/v1/brains/blobs/";

    private readonly HttpClient _http;
    private readonly string _basePrefix;
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask>? _authorize;
    private readonly TimeProvider _timeProvider;
    private readonly BrainRelayClientOptions _options;

    /// <summary>Creates a relay client targeting <paramref name="relayBaseUri"/>.</summary>
    public HttpBrainRelayClient(
        HttpClient http,
        Uri relayBaseUri,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? authorize = null,
        TimeProvider? timeProvider = null,
        BrainRelayClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(relayBaseUri);

        _http = http;
        _basePrefix = relayBaseUri.AbsoluteUri.TrimEnd('/');
        _authorize = authorize;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new BrainRelayClientOptions();
    }

    /// <summary>
    /// Posts a one-way <paramref name="envelope"/> to the relay without waiting for a reply.
    /// Compatible with <see cref="SealedRelayBrainTransport"/>'s one-way post delegate.
    /// Throws <see cref="ArgumentException"/> if <see cref="BrainEnvelope.OneWay"/> is <see langword="false"/>
    /// (the flag is cryptographically authenticated and must not be mutated after sealing).
    /// </summary>
    public async Task SendOneWayAsync(BrainEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrEmpty(envelope.MessageId);
        if (!envelope.OneWay)
        {
            throw new ArgumentException("SendOneWayAsync requires an envelope sealed with OneWay = true.", nameof(envelope));
        }

        await PostEnvelopeAsync(SendPath, envelope, ct).ConfigureAwait(false);
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
    /// Connects to <c>GET /circle/v1/brains/stream</c> over WebSocket with header-only authentication,
    /// dispatches incoming envelopes to <paramref name="receiver"/>, sends durable lease-ack/reply frames
    /// only after local delivery succeeds, handles close codes <c>1008</c> and <c>1009</c> with jittered
    /// exponential backoff, and falls back to HTTP polling when WebSocket upgrade is unavailable.
    /// </summary>
    public async Task RunStreamLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        var httpStreamUri = new Uri(_basePrefix + StreamPath);
        var wsStreamUri = ToWebSocketUri(httpStreamUri);
        var consecutiveFailures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocket? socket = null;
                try
                {
                    socket = await ConnectWebSocketAsync(httpStreamUri, wsStreamUri, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (IsWebSocketUpgradeRejected(error))
                {
                    var message = $"WebSocket upgrade unavailable ({error.GetType().Name}); falling back to HTTP polling.";
                    System.Diagnostics.Trace.TraceWarning(message);
                    _options.OnStreamDiagnostic?.Invoke(message);
                    await RunHttpFallbackWindowAsync(receiver, ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception error)
                {
                    consecutiveFailures++;
                    var message = $"WebSocket connect failed ({error.GetType().Name}); backing off (attempt {consecutiveFailures}).";
                    System.Diagnostics.Trace.TraceWarning(message);
                    _options.OnStreamDiagnostic?.Invoke(message);
                    await DelayWithJitteredBackoffAsync(consecutiveFailures, ct).ConfigureAwait(false);
                    continue;
                }

                WebSocketCloseStatus? closeStatus;
                try
                {
                    using (socket)
                    {
                        closeStatus = await ProcessWebSocketSessionAsync(socket, httpStreamUri, receiver, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    consecutiveFailures++;
                    var message = $"WebSocket session ended with error ({error.GetType().Name}); backing off (attempt {consecutiveFailures}).";
                    System.Diagnostics.Trace.TraceWarning(message);
                    _options.OnStreamDiagnostic?.Invoke(message);
                    await DelayWithJitteredBackoffAsync(consecutiveFailures, ct).ConfigureAwait(false);
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (closeStatus is WebSocketCloseStatus.PolicyViolation)
                {
                    consecutiveFailures++;
                    _options.OnStreamDiagnostic?.Invoke($"WebSocket closed with 1008 PolicyViolation; backing off (attempt {consecutiveFailures}).");
                    await DelayWithJitteredBackoffAsync(consecutiveFailures, ct).ConfigureAwait(false);
                }
                else if (closeStatus is WebSocketCloseStatus.MessageTooBig)
                {
                    consecutiveFailures++;
                    _options.OnStreamDiagnostic?.Invoke($"WebSocket closed with 1009 MessageTooBig; backing off (attempt {consecutiveFailures}).");
                    await DelayWithJitteredBackoffAsync(consecutiveFailures, ct).ConfigureAwait(false);
                }
                else
                {
                    // Clean close or server restart: reset streak after a healthy session, still apply minimal jittered backoff to prevent hot-spin.
                    consecutiveFailures = 1;
                    _options.OnStreamDiagnostic?.Invoke($"WebSocket closed ({closeStatus?.ToString() ?? "abrupt"}); reconnecting after backoff.");
                    await DelayWithJitteredBackoffAsync(consecutiveFailures, ct).ConfigureAwait(false);
                }
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
        if (!FriendCodeBundleSigner.IsLowercaseSha256Hex(codeHash))
        {
            throw new ArgumentException("codeHash must be a 64-character lowercase hex SHA-256 digest.", nameof(codeHash));
        }

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
        if (!FriendCodeBundleSigner.IsLowercaseSha256Hex(codeHash))
        {
            throw new ArgumentException("codeHash must be a 64-character lowercase hex SHA-256 digest.", nameof(codeHash));
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_basePrefix + FriendCodesPrefix + Uri.EscapeDataString(codeHash)));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Uploads opaque E2EE ciphertext bytes to <c>POST /circle/v1/brains/blobs</c> with the configured
    /// recipient ACL header and content SHA-256 header.
    /// </summary>
    public async Task<BrainBlobUploadResult> UploadBlobAsync(
        ReadOnlyMemory<byte> ciphertext,
        IReadOnlyList<BrainId> recipients,
        CancellationToken ct = default)
    {
        if (ciphertext.IsEmpty || ciphertext.Length > BrainBlobLimits.MaxBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ciphertext),
                ciphertext.Length,
                $"Blob ciphertext must be between 1 and {BrainBlobLimits.MaxBodyBytes} bytes.");
        }

        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0 || recipients.Count > BrainBlobLimits.MaxRecipients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipients),
                recipients.Count,
                $"Blob upload requires between 1 and {BrainBlobLimits.MaxRecipients} recipients.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalizedRecipients = new string[recipients.Count];
        for (var i = 0; i < recipients.Count; i++)
        {
            var id = recipients[i].Value?.Trim() ?? string.Empty;
            if (id.Length == 0
                || id.Length > BrainBlobLimits.MaxRecipientBrainIdLength
                || id.Contains(',', StringComparison.Ordinal)
                || !seen.Add(id))
            {
                throw new ArgumentException($"Invalid or duplicate recipient BrainId at index {i}.", nameof(recipients));
            }

            normalizedRecipients[i] = id;
        }

        var bytes = ciphertext.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_basePrefix + BlobsPath))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(_options.RecipientsHeaderName, string.Join(',', normalizedRecipients));
        request.Headers.TryAddWithoutValidation(_options.ContentSha256HeaderName, sha256);

        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(stream, BrainStreamJsonContext.Default.BrainBlobUploadResult, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Relay response contained a null BrainBlobUploadResult.");

        if (!string.Equals(result.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Relay returned a blob SHA-256 digest that does not match the uploaded ciphertext.");
        }

        return result;
    }

    /// <summary>
    /// Downloads opaque E2EE ciphertext bytes from <c>GET /circle/v1/brains/blobs/{blobId}</c>.
    /// Returns <see langword="null"/> on uniform <c>404 Not Found</c>, enforces size bounds,
    /// and verifies the SHA-256 digest against the response header and optional <paramref name="expectedSha256"/>.
    /// </summary>
    public async Task<BrainBlobDownloadResult?> DownloadBlobAsync(
        string blobId,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        if (!BrainBlobLimits.IsValidBlobId(blobId))
        {
            throw new ArgumentException("blobId must be a non-empty alphanumeric/dash/underscore identifier within bounds.", nameof(blobId));
        }

        if (expectedSha256 is not null && !FriendCodeBundleSigner.IsLowercaseSha256Hex(expectedSha256))
        {
            throw new ArgumentException("expectedSha256 must be a 64-character lowercase hex SHA-256 digest.", nameof(expectedSha256));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_basePrefix + BlobsPrefix + Uri.EscapeDataString(blobId)));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > BrainBlobLimits.MaxBodyBytes)
        {
            throw new InvalidDataException($"Downloaded blob exceeds maximum allowed size of {BrainBlobLimits.MaxBodyBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > BrainBlobLimits.MaxBodyBytes)
            {
                throw new InvalidDataException($"Downloaded blob exceeds maximum allowed size of {BrainBlobLimits.MaxBodyBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        var ciphertext = buffer.ToArray();
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(ciphertext));

        if (response.Headers.TryGetValues(_options.ContentSha256HeaderName, out var headerValues))
        {
            var headerSha = headerValues.FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(headerSha) && !string.Equals(headerSha, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("Downloaded blob SHA-256 does not match the relay Content-Sha256 header.");
            }
        }

        if (expectedSha256 is not null && !string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
        {
            throw new CryptographicException("Downloaded blob SHA-256 does not match the expected digest.");
        }

        return new BrainBlobDownloadResult(ciphertext, actualSha256);
    }

    private async Task<WebSocket> ConnectWebSocketAsync(Uri httpStreamUri, Uri wsStreamUri, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_options.AuthorizeWebSocketAsync is not null)
        {
            await _options.AuthorizeWebSocketAsync(httpStreamUri, headers, ct).ConfigureAwait(false);
        }
        else if (_authorize is not null)
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, httpStreamUri);
            await _authorize(probe, ct).ConfigureAwait(false);
            if (probe.Headers.Authorization is not null)
            {
                headers["Authorization"] = probe.Headers.Authorization.ToString();
            }

            foreach (var header in probe.Headers)
            {
                if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }
        }

        if (_options.WebSocketConnectAsync is not null)
        {
            return await _options.WebSocketConnectAsync(wsStreamUri, headers, ct).ConfigureAwait(false);
        }

        var ws = new ClientWebSocket();
        try
        {
            ws.Options.KeepAliveInterval = _options.KeepAliveInterval > TimeSpan.Zero
                ? _options.KeepAliveInterval
                : TimeSpan.FromSeconds(25);
            foreach (var (name, value) in headers)
            {
                ws.Options.SetRequestHeader(name, value);
            }

            await ws.ConnectAsync(wsStreamUri, ct).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private async Task<WebSocketCloseStatus?> ProcessWebSocketSessionAsync(
        WebSocket socket,
        Uri httpStreamUri,
        BrainEnvelopeReceiver receiver,
        CancellationToken ct)
    {
        var maxFrameBytes = _options.MaxInboundFrameBytes > 0 ? _options.MaxInboundFrameBytes : 2 * 1024 * 1024;
        var receiveBuffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;
            var oversized = false;

            do
            {
                result = await socket.ReceiveAsync(receiveBuffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Closed",
                                ct).ConfigureAwait(false);
                        }
                        catch (WebSocketException error)
                        {
                            System.Diagnostics.Trace.TraceWarning($"WebSocket peer terminated during close handshake ({error.WebSocketErrorCode}).");
                        }
                    }

                    return result.CloseStatus ?? socket.CloseStatus;
                }

                if (messageBuffer.Length + result.Count > maxFrameBytes)
                {
                    oversized = true;
                }
                else if (result.Count > 0)
                {
                    messageBuffer.Write(receiveBuffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (oversized)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "Inbound frame exceeded client limit.",
                        ct).ConfigureAwait(false);
                }

                return WebSocketCloseStatus.MessageTooBig;
            }

            if (result.MessageType != WebSocketMessageType.Text || messageBuffer.Length == 0)
            {
                continue;
            }

            BrainStreamFrame? frame;
            try
            {
                frame = JsonSerializer.Deserialize(
                    messageBuffer.GetBuffer().AsSpan(0, (int)messageBuffer.Length),
                    BrainStreamJsonContext.Default.BrainStreamFrame);
            }
            catch (JsonException)
            {
                _options.OnStreamDiagnostic?.Invoke("Ignored malformed JSON frame on brain relay stream.");
                continue;
            }

            if (frame is null || string.IsNullOrWhiteSpace(frame.Type))
            {
                continue;
            }

            switch (frame.Type)
            {
                case BrainStreamFrameTypes.Deliver:
                    await HandleDeliverFrameAsync(socket, frame, receiver, ct).ConfigureAwait(false);
                    break;

                case BrainStreamFrameTypes.Ping:
                    await SendStreamFrameAsync(socket, new BrainStreamFrame(BrainStreamFrameTypes.Pong), ct).ConfigureAwait(false);
                    break;

                case BrainStreamFrameTypes.AuthOk:
                case BrainStreamFrameTypes.Pong:
                case BrainStreamFrameTypes.Acked:
                    break;

                case BrainStreamFrameTypes.Error:
                    _options.OnStreamDiagnostic?.Invoke($"Relay stream error frame: {frame.Code ?? "unknown"}.");
                    if (string.Equals(frame.Code, "auth_expiring", StringComparison.OrdinalIgnoreCase)
                        && _options.CreateAuthRefreshFrameAsync is not null)
                    {
                        var refreshFrame = await _options.CreateAuthRefreshFrameAsync(httpStreamUri, ct).ConfigureAwait(false);
                        if (refreshFrame is not null)
                        {
                            await SendStreamFrameAsync(socket, refreshFrame, ct).ConfigureAwait(false);
                        }
                    }

                    break;
            }
        }

        return socket.CloseStatus;
    }

    private async Task HandleDeliverFrameAsync(
        WebSocket socket,
        BrainStreamFrame frame,
        BrainEnvelopeReceiver receiver,
        CancellationToken ct)
    {
        var incoming = frame.Envelope;
        if (incoming is null || string.IsNullOrEmpty(incoming.MessageId))
        {
            return;
        }

        BrainEnvelope reply;
        try
        {
            reply = await receiver.DeliverAsync(incoming, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Do NOT ack failed local delivery; let the server lease expire for redelivery.
            var message = $"Local envelope delivery failed ({error.GetType().Name}); skipping lease ack.";
            System.Diagnostics.Trace.TraceWarning(message);
            _options.OnStreamDiagnostic?.Invoke(message);
            return;
        }

        // Durable lease-ack: sent ONLY after local BrainEnvelopeReceiver.DeliverAsync succeeds.
        if (incoming.OneWay)
        {
            await SendStreamFrameAsync(
                socket,
                new BrainStreamFrame(
                    BrainStreamFrameTypes.Ack,
                    MessageId: incoming.MessageId,
                    LeaseId: frame.LeaseId),
                ct).ConfigureAwait(false);
        }
        else
        {
            await SendStreamFrameAsync(
                socket,
                new BrainStreamFrame(
                    BrainStreamFrameTypes.Reply,
                    Envelope: reply,
                    LeaseId: frame.LeaseId),
                ct).ConfigureAwait(false);
        }
    }

    private static async Task SendStreamFrameAsync(WebSocket socket, BrainStreamFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, BrainStreamJsonContext.Default.BrainStreamFrame);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task RunHttpFallbackWindowAsync(BrainEnvelopeReceiver receiver, CancellationToken ct)
    {
        var fallbackDuration = _options.HttpFallbackDuration > TimeSpan.Zero
            ? _options.HttpFallbackDuration
            : TimeSpan.FromSeconds(30);
        var deadline = _timeProvider.GetUtcNow() + fallbackDuration;

        while (!ct.IsCancellationRequested && _timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                var delivered = await PollOnceAsync(receiver, ct).ConfigureAwait(false);
                if (!delivered && _timeProvider.GetUtcNow() < deadline)
                {
                    var remaining = deadline - _timeProvider.GetUtcNow();
                    var pause = remaining < _options.InitialReconnectDelay ? remaining : _options.InitialReconnectDelay;
                    if (pause > TimeSpan.Zero)
                    {
                        await Task.Delay(pause, _timeProvider, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                var message = $"HTTP fallback poll failed ({error.GetType().Name}); backing off.";
                System.Diagnostics.Trace.TraceWarning(message);
                _options.OnStreamDiagnostic?.Invoke(message);
                await DelayWithJitteredBackoffAsync(1, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DelayWithJitteredBackoffAsync(int attempt, CancellationToken ct)
    {
        var delay = ComputeBackoffDelay(attempt);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    internal TimeSpan ComputeBackoffDelay(int attempt)
    {
        var initial = _options.InitialReconnectDelay > TimeSpan.Zero
            ? _options.InitialReconnectDelay
            : TimeSpan.FromSeconds(1);
        var max = _options.MaxReconnectDelay >= initial
            ? _options.MaxReconnectDelay
            : initial;

        var exponent = Math.Clamp(attempt - 1, 0, 10);
        var scaledMs = initial.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(scaledMs, max.TotalMilliseconds);

        var jitterFactor = Math.Clamp(_options.JitterFactor, 0.0, 0.5);
        if (jitterFactor > 0.0)
        {
            var sample = _options.NextRandomDouble?.Invoke() ?? Random.Shared.NextDouble();
            var clampedSample = Math.Clamp(sample, 0.0, 1.0);
            var multiplier = 1.0 + ((clampedSample * 2.0) - 1.0) * jitterFactor;
            cappedMs = Math.Max(1.0, cappedMs * multiplier);
        }

        return TimeSpan.FromMilliseconds(cappedMs);
    }

    internal static Uri ToWebSocketUri(Uri httpUri)
    {
        var builder = new UriBuilder(httpUri)
        {
            Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
        };
        return builder.Uri;
    }

    private static bool IsWebSocketUpgradeRejected(Exception error)
        => error switch
        {
            WebSocketException wsEx when wsEx.WebSocketErrorCode is WebSocketError.NotAWebSocket or WebSocketError.UnsupportedProtocol or WebSocketError.UnsupportedVersion or WebSocketError.Faulted => true,
            WebSocketException wsEx when wsEx.InnerException is HttpRequestException => true,
            HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UpgradeRequired or HttpStatusCode.NotImplemented or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable } => true,
            _ => false,
        };

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
