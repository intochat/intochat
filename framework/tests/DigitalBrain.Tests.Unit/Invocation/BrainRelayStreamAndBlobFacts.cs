using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class BrainRelayStreamAndBlobFacts
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = _timers.Where(t => !t.Disposed && t.DueAt <= _now).ToList();
                foreach (var t in due)
                {
                    _timers.Remove(t);
                }
            }

            foreach (var t in due)
            {
                t.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, GetUtcNow() + dueTime);
            lock (_gate)
            {
                if (dueTime == TimeSpan.Zero)
                {
                    timer.Fire();
                }
                else if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _timers.Add(timer);
                }
            }

            return timer;
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            public DateTimeOffset DueAt { get; private set; } = dueAt;
            public bool Disposed { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) { return false; }
                    DueAt = owner._now + dueTime;
                    if (dueTime != Timeout.InfiniteTimeSpan && !owner._timers.Contains(this))
                    {
                        owner._timers.Add(this);
                    }
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    Disposed = true;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    /// <summary>In-memory scriptable WebSocket for deterministic unit testing of stream frames and close codes.</summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Payload, WebSocketCloseStatus? CloseStatus, string? CloseDesc)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[], WebSocketCloseStatus?, string?)>();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;

        public ConcurrentQueue<string> SentTextFrames { get; } = new();
        public TaskCompletionSource<string> FirstSentFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueText(string json)
            => _inbound.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), null, null));

        public void EnqueueClose(WebSocketCloseStatus status, string description)
            => _inbound.Writer.TryWrite((WebSocketMessageType.Close, [], status, description));

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var item = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (item.Type == WebSocketMessageType.Close)
            {
                _closeStatus = item.CloseStatus;
                _closeDescription = item.CloseDesc;
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, item.CloseStatus, item.CloseDesc);
            }

            item.Payload.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(item.Payload.Length, item.Type, endOfMessage: true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
                SentTextFrames.Enqueue(text);
                FirstSentFrame.TrySetResult(text);
            }

            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
    }

    [Fact]
    public async Task WebSocketStreamAuthenticatesViaHeadersOnlyAndAcksOneWayAfterLocalDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();

        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();
        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "ws-oneway-1");
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(noteId, "bob", Role.Editor, ct);
        }

        var aliceReceiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            aliceOS.InvokeLocalAsync,
            aliceOS.PostLocalAsync,
            clock: clock);

        var request = new RemoteInvocationRequest(
            noteId,
            new RuntimeInvocation(
                string.Empty,
                "facts.transport.note",
                noteId.ToString(),
                nameof(IRemoteSecretNote.Write),
                [JsonSerializer.SerializeToElement("from-ws-stream", RuntimeProtocol.Json)]),
            "bob",
            "bob",
            bobBrain,
            OneWay: true);

        var envelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-ws-oneway-1",
            JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json),
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: clock.GetUtcNow(),
            oneWay: true);

        Uri? connectedUri = null;
        Dictionary<string, string>? capturedHeaders = null;
        var socket = new FakeWebSocket();
        var deliverFrameJson = JsonSerializer.Serialize(
            new BrainStreamFrame(
                BrainStreamFrameTypes.Deliver,
                Envelope: envelope,
                LeaseId: "lease-42",
                LeaseExpiresAt: clock.GetUtcNow().AddSeconds(30)),
            BrainStreamJsonContext.Default.BrainStreamFrame);
        socket.EnqueueText(deliverFrameJson);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            authorize: (req, _) =>
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
                req.Headers.TryAddWithoutValidation("X-Machine-Proof", "proof-jwt");
                return ValueTask.CompletedTask;
            },
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                WebSocketConnectAsync = (uri, headers, _) =>
                {
                    connectedUri = uri;
                    capturedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                    return Task.FromResult<WebSocket>(socket);
                },
            });

        var loopTask = client.RunStreamLoopAsync(aliceReceiver, loopCts.Token);
        var ackJson = await socket.FirstSentFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await loopCts.CancelAsync();
        await loopTask;

        // 1. Connected to wss://.../circle/v1/brains/stream with ZERO query-string auth.
        Assert.NotNull(connectedUri);
        Assert.Equal("wss://relay.example/circle/v1/brains/stream", connectedUri!.AbsoluteUri);
        Assert.True(string.IsNullOrEmpty(connectedUri.Query));
        Assert.NotNull(capturedHeaders);
        Assert.Equal("Bearer secret-token", capturedHeaders!["Authorization"]);
        Assert.Equal("proof-jwt", capturedHeaders["X-Machine-Proof"]);

        // 2. Durable lease-ack frame sent after local delivery succeeded.
        var ackFrame = JsonSerializer.Deserialize(ackJson, BrainStreamJsonContext.Default.BrainStreamFrame);
        Assert.NotNull(ackFrame);
        Assert.Equal(BrainStreamFrameTypes.Ack, ackFrame!.Type);
        Assert.Equal("msg-ws-oneway-1", ackFrame.MessageId);
        Assert.Equal("lease-42", ackFrame.LeaseId);

        // 3. Local neuron state reflects the delivered one-way write.
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            Assert.Equal("from-ws-stream", await aliceOS.Get<IRemoteSecretNote>(noteId).Read());
        }
    }

    [Fact]
    public async Task WebSocketStreamDoesNotAckWhenLocalDeliveryFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        var rogueBrain = new BrainId("rogue");
        using var aliceKeys = BrainKeyPair.Generate();
        using var rogueKeys = BrainKeyPair.Generate();

        await using var aliceOS = TestOS.Local();
        var aliceReceiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            _ => null, // rogue is not pinned -> DeliverAsync throws CryptographicException
            aliceOS.Edge,
            aliceOS.InvokeLocalAsync,
            aliceOS.PostLocalAsync,
            clock: clock);

        var untrustedEnvelope = BrainEnvelopeSealer.Seal(
            rogueBrain,
            aliceBrain,
            "msg-untrusted",
            "{}"u8,
            rogueKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: clock.GetUtcNow(),
            oneWay: true);

        var socket = new FakeWebSocket();
        socket.EnqueueText(JsonSerializer.Serialize(
            new BrainStreamFrame(BrainStreamFrameTypes.Deliver, Envelope: untrustedEnvelope, LeaseId: "lease-bad"),
            BrainStreamJsonContext.Default.BrainStreamFrame));
        socket.EnqueueText(JsonSerializer.Serialize(
            new BrainStreamFrame(BrainStreamFrameTypes.Ping),
            BrainStreamJsonContext.Default.BrainStreamFrame));

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                WebSocketConnectAsync = (_, _, _) => Task.FromResult<WebSocket>(socket),
            });

        var loopTask = client.RunStreamLoopAsync(aliceReceiver, loopCts.Token);
        // The first sent frame must be pong (from the subsequent ping), NOT an ack for msg-untrusted.
        var firstFrameJson = await socket.FirstSentFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await loopCts.CancelAsync();
        await loopTask;

        var frame = JsonSerializer.Deserialize(firstFrameJson, BrainStreamJsonContext.Default.BrainStreamFrame);
        Assert.NotNull(frame);
        Assert.Equal(BrainStreamFrameTypes.Pong, frame!.Type);
        Assert.DoesNotContain(socket.SentTextFrames, f => f.Contains("\"ack\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.PolicyViolation)]
    [InlineData(WebSocketCloseStatus.MessageTooBig)]
    public async Task WebSocketStreamHandles1008And1009WithExponentialBackoffWithoutHotSpin(WebSocketCloseStatus closeStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        using var aliceKeys = BrainKeyPair.Generate();
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            _ => null,
            aliceOS.Edge,
            aliceOS.InvokeLocalAsync,
            aliceOS.PostLocalAsync,
            clock: clock);

        var connectCount = 0;
        var secondConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));

        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                InitialReconnectDelay = TimeSpan.FromSeconds(2),
                MaxReconnectDelay = TimeSpan.FromSeconds(16),
                JitterFactor = 0.0,
                WebSocketConnectAsync = (_, _, _) =>
                {
                    var attempt = Interlocked.Increment(ref connectCount);
                    var ws = new FakeWebSocket();
                    if (attempt == 1)
                    {
                        ws.EnqueueClose(closeStatus, "test-close");
                    }
                    else
                    {
                        secondConnect.TrySetResult();
                    }

                    return Task.FromResult<WebSocket>(ws);
                },
            });

        var loopTask = client.RunStreamLoopAsync(receiver, loopCts.Token);

        // Give the first attempt a moment to process the close and enter the 2s backoff delay.
        await Task.Delay(50, ct);
        Assert.Equal(1, Volatile.Read(ref connectCount));

        // Advance FakeTimeProvider by 1s (still inside 2s backoff -> must NOT reconnect yet).
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, ct);
        Assert.Equal(1, Volatile.Read(ref connectCount));

        // Advance past the 2s backoff -> reconnects cleanly.
        clock.Advance(TimeSpan.FromSeconds(2));
        await secondConnect.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(2, Volatile.Read(ref connectCount));

        await loopCts.CancelAsync();
        await loopTask;
    }

    [Fact]
    public async Task WebSocketUpgradeRejectionFallsBackGracefullyToHttpPolling()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        using var aliceKeys = BrainKeyPair.Generate();
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            _ => null,
            aliceOS.Edge,
            aliceOS.InvokeLocalAsync,
            aliceOS.PostLocalAsync,
            clock: clock);

        var httpPollTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wsAttempts = 0;

        using var http = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == "/circle/v1/brains/next")
            {
                httpPollTcs.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                InitialReconnectDelay = TimeSpan.FromSeconds(1),
                HttpFallbackDuration = TimeSpan.FromSeconds(10),
                WebSocketConnectAsync = (_, _, _) =>
                {
                    Interlocked.Increment(ref wsAttempts);
                    return Task.FromException<WebSocket>(new WebSocketException(WebSocketError.NotAWebSocket, "Proxy rejected upgrade."));
                },
            });

        var loopTask = client.RunStreamLoopAsync(receiver, loopCts.Token);
        await httpPollTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await loopCts.CancelAsync();
        await loopTask;

        Assert.True(wsAttempts >= 1);
    }

    [Fact]
    public async Task BlobUploadAndDownloadSendExpectedHeadersVerifySha256AndHandleUniform404()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var ciphertext = "opaque-sealed-media-bytes"u8.ToArray();
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(ciphertext));
        string? capturedRecipients = null;
        string? capturedShaHeader = null;
        string? capturedContentType = null;

        using var http = new HttpClient(new StubHttpHandler(async (req, token) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath == "/circle/v1/brains/blobs")
            {
                capturedRecipients = req.Headers.GetValues("X-IntoChat-Recipients").Single();
                capturedShaHeader = req.Headers.GetValues("X-IntoChat-Content-Sha256").Single();
                capturedContentType = req.Content?.Headers.ContentType?.MediaType;
                var body = await req.Content!.ReadAsByteArrayAsync(token);
                Assert.Equal(ciphertext, body);

                var json = JsonSerializer.Serialize(
                    new BrainBlobUploadResult("blob-abc123", expectedSha, body.Length, clock.GetUtcNow().AddDays(7)),
                    BrainStreamJsonContext.Default.BrainBlobUploadResult);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == "/circle/v1/brains/blobs/blob-abc123")
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ciphertext),
                };
                res.Headers.TryAddWithoutValidation("X-IntoChat-Content-Sha256", expectedSha);
                res.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
                return res;
            }

            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == "/circle/v1/brains/blobs/missing-blob")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == "/circle/v1/brains/blobs/tampered-blob")
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("tampered-bytes"u8.ToArray()),
                };
                res.Headers.TryAddWithoutValidation("X-IntoChat-Content-Sha256", expectedSha);
                return res;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                RecipientsHeaderName = "X-IntoChat-Recipients",
                ContentSha256HeaderName = "X-IntoChat-Content-Sha256",
            });
        Assert.IsAssignableFrom<IBrainRelayClient>(client);

        var uploaded = await client.UploadBlobAsync(ciphertext, [new BrainId("bob"), new BrainId("carol")], ct);
        Assert.Equal("blob-abc123", uploaded.BlobId);
        Assert.Equal(expectedSha, uploaded.Sha256);
        Assert.Equal(ciphertext.Length, uploaded.SizeBytes);
        Assert.Equal("bob,carol", capturedRecipients);
        Assert.Equal(expectedSha, capturedShaHeader);
        Assert.Equal("application/octet-stream", capturedContentType);

        var downloaded = await client.DownloadBlobAsync("blob-abc123", expectedSha, ct);
        Assert.NotNull(downloaded);
        Assert.Equal(ciphertext, downloaded!.Ciphertext);
        Assert.Equal(expectedSha, downloaded.Sha256);

        // Uniform 404 returns null cleanly.
        Assert.Null(await client.DownloadBlobAsync("missing-blob", ct: ct));

        // Tampered ciphertext rejected fail-closed with CryptographicException.
        await Assert.ThrowsAsync<CryptographicException>(() => client.DownloadBlobAsync("tampered-blob", expectedSha, ct));

        // Invalid inputs rejected client-side.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UploadBlobAsync(ReadOnlyMemory<byte>.Empty, [new BrainId("bob")], ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadBlobAsync(ciphertext, [new BrainId("bob"), new BrainId("bob")], ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadBlobAsync("../escape", ct: ct));
    }

    [Fact]
    public async Task BrainBlobSealerRoundTripsMultiRecipientAndRejectsTamperOrUnauthorizedRecipient()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        var carolBrain = new BrainId("carol");
        var eveBrain = new BrainId("eve");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        using var carolKeys = BrainKeyPair.Generate();
        using var eveKeys = BrainKeyPair.Generate();

        var plaintext = Encoding.UTF8.GetBytes("voice-note-e2ee-secret-payload-12345");
        var recipients = new (BrainId, BrainPublicKeys)[]
        {
            (bobBrain, bobKeys.ExportPublicKeys()),
            (carolBrain, carolKeys.ExportPublicKeys()),
        };

        // Seal using non-exportable IBrainKeySigner path.
        var sealedBlob = await BrainBlobSealer.SealAsync(
            aliceBrain,
            new BrainKeyPairSigner(aliceKeys),
            plaintext,
            recipients,
            ct);

        // Zero plaintext leakage on wire.
        Assert.DoesNotContain("voice-note-e2ee-secret-payload-12345", Encoding.UTF8.GetString(sealedBlob), StringComparison.Ordinal);

        // Both authorized recipients decrypt identical plaintext.
        var bobPlain = await BrainBlobSealer.UnsealAsync(
            sealedBlob,
            bobBrain,
            new BrainKeyPairSigner(bobKeys),
            aliceKeys.ExportPublicKeys(),
            ct);
        var carolPlain = BrainBlobSealer.Unseal(
            sealedBlob,
            carolBrain,
            carolKeys,
            aliceKeys.ExportPublicKeys());

        Assert.Equal(plaintext, bobPlain);
        Assert.Equal(plaintext, carolPlain);

        // Unauthorized recipient (Eve) is rejected fail-closed.
        await Assert.ThrowsAsync<CryptographicException>(() =>
            BrainBlobSealer.UnsealAsync(sealedBlob, eveBrain, new BrainKeyPairSigner(eveKeys), aliceKeys.ExportPublicKeys(), ct).AsTask());

        // Wrong expected sender public key is rejected fail-closed.
        Assert.Throws<CryptographicException>(() =>
            BrainBlobSealer.Unseal(sealedBlob, bobBrain, bobKeys, eveKeys.ExportPublicKeys()));

        // Bit-flip tamper on ciphertext is rejected fail-closed.
        var tamperedCiphertext = (byte[])sealedBlob.Clone();
        tamperedCiphertext[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() =>
            BrainBlobSealer.Unseal(tamperedCiphertext, bobBrain, bobKeys, aliceKeys.ExportPublicKeys()));

        // Bit-flip tamper inside signed header is rejected fail-closed.
        var tamperedHeader = (byte[])sealedBlob.Clone();
        tamperedHeader[10] ^= 0x01;
        Assert.Throws<CryptographicException>(() =>
            BrainBlobSealer.Unseal(tamperedHeader, bobBrain, bobKeys, aliceKeys.ExportPublicKeys()));
    }
}
