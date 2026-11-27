using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
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
        private readonly Channel<(WebSocketMessageType Type, byte[] Payload, bool EndOfMessage, WebSocketCloseStatus? CloseStatus, string? CloseDesc)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[], bool, WebSocketCloseStatus?, string?)>();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        private int _receiveCount;

        public ConcurrentQueue<string> SentTextFrames { get; } = new();
        public TaskCompletionSource<string> FirstSentFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseOutputStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Terminated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangCloseOutput { get; set; }
        public int ReceiveCount => Volatile.Read(ref _receiveCount);
        public bool CloseAsyncCalled { get; private set; }
        public bool CloseOutputCalled { get; private set; }
        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueText(string json)
            => _inbound.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), true, null, null));

        public void EnqueueTextFragment(byte[] payload, bool endOfMessage)
            => _inbound.Writer.TryWrite((WebSocketMessageType.Text, payload, endOfMessage, null, null));

        public void EnqueueClose(WebSocketCloseStatus status, string description)
            => _inbound.Writer.TryWrite((WebSocketMessageType.Close, [], true, status, description));

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var item = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _receiveCount);
            if (item.Type == WebSocketMessageType.Close)
            {
                _closeStatus = item.CloseStatus;
                _closeDescription = item.CloseDesc;
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, item.CloseStatus, item.CloseDesc);
            }

            item.Payload.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(item.Payload.Length, item.Type, endOfMessage: item.EndOfMessage);
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
            CloseAsyncCalled = true;
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            Terminated.TrySetResult();
            return Task.CompletedTask;
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            CloseOutputStarted.TrySetResult();
            if (HangCloseOutput)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            _state = WebSocketState.CloseSent;
            Terminated.TrySetResult();
        }

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            Terminated.TrySetResult();
        }

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
    public async Task OversizedWebSocketFrameBreaksImmediatelyAndClosesOutputWithoutUnboundedDrain()
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

        var socket = new FakeWebSocket();
        var chunk = new byte[32];
        for (var i = 0; i < 100; i++)
        {
            socket.EnqueueTextFragment(chunk, endOfMessage: false);
        }

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                MaxInboundFrameBytes = 64,
                InitialReconnectDelay = TimeSpan.FromSeconds(10),
                JitterFactor = 0.0,
                WebSocketConnectAsync = (_, _, _) => Task.FromResult<WebSocket>(socket),
            });

        var loopTask = client.RunStreamLoopAsync(receiver, loopCts.Token);
        await socket.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await loopCts.CancelAsync();
        await loopTask;

        // 3 chunks of 32 bytes = 96 > 64 -> must break immediately without draining remaining 97 fragments.
        Assert.Equal(3, socket.ReceiveCount);
        Assert.True(socket.CloseOutputCalled);
        Assert.False(socket.CloseAsyncCalled);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    [Fact]
    public async Task OversizedWebSocketFrameAbortsSocketWhenCloseOutputTimesOut()
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

        var socket = new FakeWebSocket { HangCloseOutput = true };
        socket.EnqueueTextFragment(new byte[128], endOfMessage: false);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var client = new HttpBrainRelayClient(
            http,
            new Uri("https://relay.example"),
            timeProvider: clock,
            options: new BrainRelayClientOptions
            {
                MaxInboundFrameBytes = 64,
                InitialReconnectDelay = TimeSpan.FromSeconds(10),
                JitterFactor = 0.0,
                WebSocketConnectAsync = (_, _, _) => Task.FromResult<WebSocket>(socket),
            });

        var loopTask = client.RunStreamLoopAsync(receiver, loopCts.Token);
        await socket.CloseOutputStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Advance clock past the bounded CloseOutputAsync timeout (2s) -> must fall back to Abort().
        clock.Advance(TimeSpan.FromSeconds(3));
        await socket.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await loopCts.CancelAsync();
        await loopTask;

        Assert.Equal(1, socket.ReceiveCount);
        Assert.True(socket.CloseOutputCalled);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public void IsWebSocketUpgradeRejectedClassifiesOnlyGenuineUpgradeRejections()
    {
        // Genuine upgrade rejections -> true.
        Assert.True(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.NotAWebSocket)));
        Assert.True(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.UnsupportedProtocol)));
        Assert.True(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.UnsupportedVersion)));
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.UpgradeRequired, HttpStatusCode.NotImplemented })
        {
            var httpEx = new HttpRequestException($"HTTP {(int)status}", null, status);
            Assert.True(HttpBrainRelayClient.IsWebSocketUpgradeRejected(httpEx), $"Expected {status} to be classified as upgrade rejection.");
            Assert.True(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.NotAWebSocket, httpEx)), $"Expected wrapped {status} to be classified as upgrade rejection.");
        }

        // Transient, network (DNS/TCP/TLS), and auth/rate-limit errors -> false.
        Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.Faulted)));
        var dnsEx = new HttpRequestException("DNS failure", new SocketException((int)SocketError.HostNotFound));
        var tcpEx = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        var tlsEx = new HttpRequestException("TLS handshake failed", new AuthenticationException("Untrusted cert"));
        foreach (var networkEx in new[] { dnsEx, tcpEx, tlsEx })
        {
            Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(networkEx));
            Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.Faulted, networkEx)));
            Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.NotAWebSocket, networkEx)));
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable })
        {
            var httpEx = new HttpRequestException($"HTTP {(int)status}", null, status);
            Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(httpEx), $"Expected {status} NOT to be classified as upgrade rejection.");
            Assert.False(HttpBrainRelayClient.IsWebSocketUpgradeRejected(new WebSocketException(WebSocketError.NotAWebSocket, httpEx)), $"Expected wrapped {status} NOT to be classified as upgrade rejection.");
        }
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("tcp")]
    [InlineData("tls")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("502")]
    [InlineData("503")]
    public async Task WebSocketTransientAndAuthErrorsBackOffWithoutHttpFallback(string errorKind)
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

        Exception CreateError() => errorKind switch
        {
            "dns" => new WebSocketException(WebSocketError.Faulted, new HttpRequestException("DNS failed", new SocketException((int)SocketError.HostNotFound))),
            "tcp" => new WebSocketException(WebSocketError.Faulted, new HttpRequestException("TCP refused", new SocketException((int)SocketError.ConnectionRefused))),
            "tls" => new WebSocketException(WebSocketError.Faulted, new HttpRequestException("TLS failed", new AuthenticationException("cert"))),
            "401" => new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized),
            "403" => new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden),
            "429" => new HttpRequestException("TooManyRequests", null, HttpStatusCode.TooManyRequests),
            "502" => new HttpRequestException("BadGateway", null, HttpStatusCode.BadGateway),
            "503" => new HttpRequestException("ServiceUnavailable", null, HttpStatusCode.ServiceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind)),
        };

        var httpPollCount = 0;
        var wsAttempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var http = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/circle/v1/brains/next")
            {
                Interlocked.Increment(ref httpPollCount);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                    var attempt = Interlocked.Increment(ref wsAttempts);
                    if (attempt == 2)
                    {
                        secondAttempt.TrySetResult();
                    }
                    else if (attempt == 3)
                    {
                        thirdAttempt.TrySetResult();
                    }

                    return Task.FromException<WebSocket>(CreateError());
                },
            });

        var loopTask = client.RunStreamLoopAsync(receiver, loopCts.Token);

        await Task.Delay(50, ct);
        Assert.Equal(1, Volatile.Read(ref wsAttempts));
        Assert.Equal(0, Volatile.Read(ref httpPollCount));

        // 1s < 2s initial backoff -> still waiting.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, ct);
        Assert.Equal(1, Volatile.Read(ref wsAttempts));

        // 2s total -> triggers attempt 2.
        clock.Advance(TimeSpan.FromSeconds(1));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(2, Volatile.Read(ref wsAttempts));

        // Attempt 2 backs off for 4s -> 3s is not enough.
        clock.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(50, ct);
        Assert.Equal(2, Volatile.Read(ref wsAttempts));

        // 4s total -> triggers attempt 3; still zero HTTP fallback polls.
        clock.Advance(TimeSpan.FromSeconds(1));
        await thirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(0, Volatile.Read(ref httpPollCount));

        await loopCts.CancelAsync();
        await loopTask;
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
