using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class HttpBrainRelayEndToEndFacts
{
    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? instance : null;
    }

    private sealed class LoopbackBrainRelayServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentDictionary<string, Channel<string>> _inboxes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _replies = new(StringComparer.Ordinal);

        public Uri BaseUri { get; }
        public ConcurrentQueue<string> CapturedBodies { get; } = new();

        private LoopbackBrainRelayServer(int port)
        {
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            _listener.Start();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static LoopbackBrainRelayServer Start()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return new LoopbackBrainRelayServer(port);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;
                var path = req.Url?.AbsolutePath ?? string.Empty;

                if (req.HttpMethod == "POST" && path == "/circle/v1/brains/send")
                {
                    var body = await ReadBodyAsync(req, ct).ConfigureAwait(false);
                    CapturedBodies.Enqueue(body);

                    using var doc = JsonDocument.Parse(body);
                    var to = doc.RootElement.GetProperty("to").GetString()
                        ?? throw new InvalidOperationException("Missing 'to' field.");

                    var inbox = _inboxes.GetOrAdd(to, static _ => Channel.CreateUnbounded<string>());
                    await inbox.Writer.WriteAsync(body, ct).ConfigureAwait(false);

                    res.StatusCode = (int)HttpStatusCode.OK;
                    res.Close();
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/circle/v1/brains/next")
                {
                    var brainId = req.Headers["X-Brain-Id"];
                    if (string.IsNullOrEmpty(brainId))
                    {
                        res.StatusCode = (int)HttpStatusCode.Unauthorized;
                        res.Close();
                        return;
                    }

                    var inbox = _inboxes.GetOrAdd(brainId, static _ => Channel.CreateUnbounded<string>());
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromMilliseconds(100));

                    try
                    {
                        if (await inbox.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false)
                            && inbox.Reader.TryRead(out var payload))
                        {
                            await WriteJsonAsync(res, payload, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout -> 204 No Content
                    }

                    res.StatusCode = (int)HttpStatusCode.NoContent;
                    res.Close();
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/circle/v1/brains/reply")
                {
                    var body = await ReadBodyAsync(req, ct).ConfigureAwait(false);
                    CapturedBodies.Enqueue(body);

                    using var doc = JsonDocument.Parse(body);
                    var replyTo = doc.RootElement.GetProperty("replyToMessageId").GetString()
                        ?? throw new InvalidOperationException("Missing 'replyToMessageId' field.");

                    var tcs = _replies.GetOrAdd(replyTo, static _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                    tcs.TrySetResult(body);

                    res.StatusCode = (int)HttpStatusCode.OK;
                    res.Close();
                    return;
                }

                if (req.HttpMethod == "GET" && path.StartsWith("/circle/v1/brains/reply/", StringComparison.Ordinal))
                {
                    var messageId = Uri.UnescapeDataString(path["/circle/v1/brains/reply/".Length..]);
                    var tcs = _replies.GetOrAdd(messageId, static _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromMilliseconds(100));

                    try
                    {
                        var replyJson = await tcs.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
                        _replies.TryRemove(messageId, out _);
                        await WriteJsonAsync(res, replyJson, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        res.StatusCode = (int)HttpStatusCode.NoContent;
                        res.Close();
                        return;
                    }
                }

                res.StatusCode = (int)HttpStatusCode.NotFound;
                res.Close();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception)
            {
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.Close();
                }
                catch
                {
                    // Ignore response close errors during failure.
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            _listener.Close();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task BobCallsAlicesOfferedNeuronOverRealHttpRelayWithZeroPlaintextOnWire()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var publicDirectory = new Dictionary<BrainId, BrainPublicKeys>
        {
            [aliceBrain] = aliceKeys.ExportPublicKeys(),
            [bobBrain] = bobKeys.ExportPublicKeys(),
        };

        await using var relayServer = LoopbackBrainRelayServer.Start();
        using var aliceHttp = new HttpClient();
        using var bobHttp = new HttpClient();

        var aliceClient = new HttpBrainRelayClient(
            aliceHttp,
            relayServer.BaseUri,
            (request, _) =>
            {
                request.Headers.Add("X-Brain-Id", aliceBrain.Value);
                return ValueTask.CompletedTask;
            });

        var bobClient = new HttpBrainRelayClient(
            bobHttp,
            relayServer.BaseUri,
            (request, _) =>
            {
                request.Headers.Add("X-Brain-Id", bobBrain.Value);
                return ValueTask.CompletedTask;
            });

        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();

        var aliceReceiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => publicDirectory.TryGetValue(brain, out var keys) ? keys : null,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token),
            (target, call) => aliceOS.PostLocalAsync(target, call));

        TestOS? bobOSRef = null;
        var bobTransport = new SealedRelayBrainTransport(
            bobBrain,
            bobKeys,
            brain => publicDirectory.TryGetValue(brain, out var keys) ? keys : null,
            new DelegatingCallerAmbient(() => bobOSRef!.Callers),
            bobClient.SendAndWaitReplyAsync);

        await using var bobOS = TestOS.Local(options =>
        {
            options.Services = new SingleServiceProvider(typeof(IBrainTransport), bobTransport);
        });
        bobOSRef = bobOS;

        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "http-note-1");
        const string secretText = "secret-over-http";

        // Alice writes her secret note locally and offers it to Bob as Viewer.
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            await aliceOS.Get<IRemoteSecretNote>(noteId).Write(secretText);
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(noteId, "bob", Role.Viewer, ct);
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = aliceClient.RunPollLoopAsync(aliceReceiver, pollCts.Token);

        try
        {
            // Bob calls Alice's offered neuron from bobOS over real HTTP.
            using (bobOS.Edge.Enter(bobOS.Edge.Person("bob", "bob", bobBrain)))
            {
                var read = await bobOS.Get<IRemoteSecretNote>(noteId).Read();
                Assert.Equal(secretText, read);

                // Every HTTP body captured on the wire must be encrypted and contain ZERO plaintext.
                var captured = relayServer.CapturedBodies.ToArray();
                Assert.True(captured.Length >= 2, $"Expected at least 2 captured HTTP bodies (request + reply), got {captured.Length}.");
                foreach (var body in captured)
                {
                    Assert.DoesNotContain(secretText, body, StringComparison.Ordinal);
                    Assert.Contains("\"ciphertext\"", body, StringComparison.Ordinal);
                    Assert.Contains("\"signature\"", body, StringComparison.Ordinal);
                }

                // Bob's write call must be refused by Alice's AccessGate (offer.read-only).
                var writeError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    bobOS.Get<IRemoteSecretNote>(noteId).Write("tamper"));
                Assert.Contains("offer.read-only", writeError.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await pollCts.CancelAsync();
            await pollTask;
        }
    }
}
