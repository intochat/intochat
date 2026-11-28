using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Invocation;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// Real Kestrel loopback (<c>http://127.0.0.1:&lt;ephemeral-port&gt;</c> / <c>ws://127.0.0.1:&lt;ephemeral-port&gt;</c>)
/// Cloud Circle relay test host implementing the exact wire contract over real TCP/HTTP/WebSocket with zero
/// in-memory transport shortcuts:
/// <list type="bullet">
///   <item><c>POST /circle/v1/friends/codes</c>, <c>POST /circle/v1/friends/codes/resolve</c>, <c>DELETE /circle/v1/friends/codes/{codeHash}</c></item>
///   <item><c>POST /circle/v1/brains/send</c>, <c>GET /circle/v1/brains/stream</c> (WebSocket), <c>GET /circle/v1/brains/next</c>, <c>POST /circle/v1/brains/reply</c>, <c>GET /circle/v1/brains/reply/{messageId}</c>, <c>POST /circle/v1/brains/ack[/{messageId}]</c></item>
///   <item><c>POST /circle/v1/brains/blobs</c> &amp; <c>GET /circle/v1/brains/blobs/{blobId}</c> with <c>X-IntoChat-Recipients</c> ACL (uniform 404) &amp; <c>X-IntoChat-Content-Sha256</c> integrity</item>
///   <item><c>PUT /circle/v1/compute/capability</c>, <c>GET /circle/v1/compute/nodes</c>, <c>GET /circle/v1/compute/engineering/next</c>, <c>POST /circle/v1/compute/engineering/dispatch</c> (fail-closed 403), <c>POST /circle/v1/compute/engineering/{jobId}/stream</c> (NDJSON), <c>POST /circle/v1/compute/engineering/{jobId}/result</c></item>
/// </list>
/// </summary>
internal sealed class LoopbackCloudRelayTestHost : IAsyncDisposable
{
    public const string BrainIdHeader = "X-Brain-Id";

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebApplication _app;

    // Friend-code rendezvous state (keyed strictly by SHA-256 codeHash)
    private readonly ConcurrentDictionary<string, FriendCodeBundle> _friendCodes = new(StringComparer.Ordinal);

    // Brain sealed relay queues, active WS streams, replies, and acks
    private readonly ConcurrentDictionary<string, Channel<BrainEnvelope>> _inboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrainEnvelope>> _replies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _ackedMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeWebSockets = new(StringComparer.Ordinal);

    // Opaque E2EE media blobs
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);

    // Paired PC Plane 2 compute relay state
    private readonly ConcurrentDictionary<(string BrainId, string MachineId), ComputeCapabilityAdvertisement> _capabilities = new();
    private readonly ConcurrentDictionary<string, Channel<ConnectedEngineeringRequest>> _engineeringQueues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingEngineeringJob> _pendingEngineeringJobs = new(StringComparer.Ordinal);

    // Wire capture buffers for zero-plaintext and protocol assertions
    public ConcurrentQueue<string> CapturedHttpBodies { get; } = new();
    public ConcurrentQueue<string> CapturedWebSocketFrames { get; } = new();
    public ConcurrentQueue<BrainEnvelope> CapturedEnvelopes { get; } = new();
    public ConcurrentQueue<string> PublishedCodeHashes { get; } = new();
    public ConcurrentQueue<StoredBlobRecord> CapturedBlobs { get; } = new();
    public ConcurrentDictionary<string, ConcurrentQueue<EngineeringStreamChunk>> CapturedEngineeringStreamChunks { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, ConnectedEngineeringResponse> CapturedEngineeringResults { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional test hook to mutate the <see cref="ConnectedEngineeringResponse"/> returned to the Cloud caller over HTTP.</summary>
    public Func<ConnectedEngineeringResponse, ConnectedEngineeringResponse>? OnEngineeringResponseMutator { get; set; }

    public Uri BaseUri { get; }
    public string Origin => BaseUri.AbsoluteUri.TrimEnd('/');

    private LoopbackCloudRelayTestHost(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public int ActiveWebSocketCount(string brainId)
        => _activeWebSockets.TryGetValue(brainId, out var count) ? count : 0;

    public async Task WaitForWebSocketConnectedAsync(string brainId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ActiveWebSocketCount(brainId) > 0)
            {
                return;
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for WebSocket stream connection for brain '{brainId}'.");
    }

    public static async Task<LoopbackCloudRelayTestHost> StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        var host = new LoopbackCloudRelayTestHost(app, new Uri("http://127.0.0.1/"));
        host.MapRoutes(app);

        await app.StartAsync(ct).ConfigureAwait(false);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var listeningAddress = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("LoopbackCloudRelayTestHost failed to bind an ephemeral loopback port.");

        var resolvedUri = new Uri(listeningAddress.TrimEnd('/') + "/");
        return new LoopbackCloudRelayTestHost(app, resolvedUri, host);
    }

    private LoopbackCloudRelayTestHost(WebApplication app, Uri baseUri, LoopbackCloudRelayTestHost stateSource)
    {
        _app = app;
        BaseUri = baseUri;
        _friendCodes = stateSource._friendCodes;
        _inboxes = stateSource._inboxes;
        _replies = stateSource._replies;
        _ackedMessages = stateSource._ackedMessages;
        _activeWebSockets = stateSource._activeWebSockets;
        _blobs = stateSource._blobs;
        _capabilities = stateSource._capabilities;
        _engineeringQueues = stateSource._engineeringQueues;
        _pendingEngineeringJobs = stateSource._pendingEngineeringJobs;
        CapturedHttpBodies = stateSource.CapturedHttpBodies;
        CapturedWebSocketFrames = stateSource.CapturedWebSocketFrames;
        CapturedEnvelopes = stateSource.CapturedEnvelopes;
        PublishedCodeHashes = stateSource.PublishedCodeHashes;
        CapturedBlobs = stateSource.CapturedBlobs;
        CapturedEngineeringStreamChunks = stateSource.CapturedEngineeringStreamChunks;
        CapturedEngineeringResults = stateSource.CapturedEngineeringResults;
        stateSource._delegate = this;
    }

    private LoopbackCloudRelayTestHost? _delegate;
    private Func<ConnectedEngineeringResponse, ConnectedEngineeringResponse>? EffectiveMutator
        => _delegate?.OnEngineeringResponseMutator ?? OnEngineeringResponseMutator;

    private void MapRoutes(WebApplication app)
    {
        // =====================================================================
        // 1. Friend-code rendezvous (/circle/v1/friends/codes*)
        // =====================================================================
        app.MapPost("/circle/v1/friends/codes", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var bundle = JsonSerializer.Deserialize<FriendCodeBundle>(raw, WireJson);
            if (bundle is null || !FriendCodeBundleSigner.IsLowercaseSha256Hex(bundle.CodeHash))
            {
                return Results.BadRequest();
            }

            _friendCodes[bundle.CodeHash] = bundle;
            PublishedCodeHashes.Enqueue(bundle.CodeHash);
            return Results.Ok();
        });

        app.MapPost("/circle/v1/friends/codes/resolve", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var req = JsonSerializer.Deserialize<ResolveFriendCodeRequest>(raw, WireJson);
            if (req is null || !FriendCodeBundleSigner.IsLowercaseSha256Hex(req.CodeHash))
            {
                return Results.BadRequest();
            }

            if (!_friendCodes.TryGetValue(req.CodeHash, out var bundle))
            {
                return Results.NotFound();
            }

            var response = new ResolveFriendCodeResponse(
                bundle.BrainId,
                bundle.SigningPublicKey,
                bundle.ExchangePublicKey,
                bundle.SelfSignature,
                bundle.RelayOrigin,
                bundle.ExpiresAt);
            return Results.Text(JsonSerializer.Serialize(response, WireJson), "application/json");
        });

        app.MapDelete("/circle/v1/friends/codes/{codeHash}", (HttpContext ctx, string codeHash) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            return _friendCodes.TryRemove(codeHash, out _)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // =====================================================================
        // 2. Sealed Brain Relay (HTTP + Real WebSocket /circle/v1/brains/*)
        // =====================================================================
        app.MapPost("/circle/v1/brains/send", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var envelope = JsonSerializer.Deserialize<BrainEnvelope>(raw, RuntimeProtocol.Json);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.To.Value))
            {
                return Results.BadRequest();
            }

            CapturedEnvelopes.Enqueue(envelope);
            var inbox = _inboxes.GetOrAdd(envelope.To.Value, static _ => Channel.CreateUnbounded<BrainEnvelope>());
            await inbox.Writer.WriteAsync(envelope, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Ok();
        });

        app.MapGet("/circle/v1/brains/stream", async ctx =>
        {
            // Strict header-only auth: reject query-string tokens or missing auth.
            if (ctx.Request.Query.ContainsKey("token")
                || ctx.Request.Query.ContainsKey("access_token")
                || !TryAuthenticateBrain(ctx, out var brainId))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            _activeWebSockets.AddOrUpdate(brainId, 1, static (_, c) => c + 1);
            try
            {
                await HandleBrainWebSocketAsync(ws, brainId, ctx.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                _activeWebSockets.AddOrUpdate(brainId, 0, static (_, c) => Math.Max(0, c - 1));
            }
        });

        app.MapGet("/circle/v1/brains/next", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out var brainId))
            {
                return Results.Unauthorized();
            }

            var inbox = _inboxes.GetOrAdd(brainId, static _ => Channel.CreateUnbounded<BrainEnvelope>());
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            waitCts.CancelAfter(TimeSpan.FromMilliseconds(100));

            try
            {
                if (await inbox.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false)
                    && inbox.Reader.TryRead(out var env))
                {
                    ctx.Response.Headers["X-Brain-Lease-Id"] = "lease-" + Guid.NewGuid().ToString("N")[..12];
                    return Results.Text(JsonSerializer.Serialize(env, RuntimeProtocol.Json), "application/json");
                }
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                // Long-poll timeout -> 204
            }

            return Results.NoContent();
        });

        app.MapPost("/circle/v1/brains/reply", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var reply = JsonSerializer.Deserialize<BrainEnvelope>(raw, RuntimeProtocol.Json);
            if (reply is null || string.IsNullOrWhiteSpace(reply.ReplyToMessageId))
            {
                return Results.BadRequest();
            }

            CapturedEnvelopes.Enqueue(reply);
            var tcs = _replies.GetOrAdd(
                reply.ReplyToMessageId,
                static _ => new TaskCompletionSource<BrainEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously));
            tcs.TrySetResult(reply);
            return Results.Ok();
        });

        app.MapGet("/circle/v1/brains/reply/{messageId}", async (HttpContext ctx, string messageId) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var tcs = _replies.GetOrAdd(
                messageId,
                static _ => new TaskCompletionSource<BrainEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously));

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            waitCts.CancelAfter(TimeSpan.FromMilliseconds(200));

            try
            {
                var reply = await tcs.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
                _replies.TryRemove(messageId, out _);
                return Results.Text(JsonSerializer.Serialize(reply, RuntimeProtocol.Json), "application/json");
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.NoContent();
            }
        });

        app.MapPost("/circle/v1/brains/ack", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                _ackedMessages[raw] = 1;
            }

            return Results.NoContent();
        });

        app.MapPost("/circle/v1/brains/ack/{messageId}", (HttpContext ctx, string messageId) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            _ackedMessages[messageId] = 1;
            return Results.NoContent();
        });

        // =====================================================================
        // 3. E2EE Media Blobs (/circle/v1/brains/blobs*)
        // =====================================================================
        app.MapPost("/circle/v1/brains/blobs", async (HttpContext ctx) =>
        {
            if (!TryAuthenticateBrain(ctx, out _))
            {
                return Results.Unauthorized();
            }

            var recipientsHeader = ctx.Request.Headers[BrainRelayTransportHosting.CircleBlobRecipientsHeader].ToString();
            var contentShaHeader = ctx.Request.Headers[BrainRelayTransportHosting.CircleBlobContentSha256Header].ToString();

            var recipients = recipientsHeader
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            if (recipients.Count == 0 || string.IsNullOrWhiteSpace(contentShaHeader))
            {
                return Results.BadRequest();
            }

            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
            var ciphertext = ms.ToArray();
            var actualSha = Convert.ToHexStringLower(SHA256.HashData(ciphertext));
            if (!string.Equals(actualSha, contentShaHeader.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest();
            }

            var blobId = "blob_" + Guid.NewGuid().ToString("N")[..16];
            var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
            _blobs[blobId] = new StoredBlob(ciphertext, actualSha, recipients, expiresAt);
            CapturedBlobs.Enqueue(new StoredBlobRecord(blobId, [.. recipients], actualSha, ciphertext));

            var result = new BrainBlobUploadResult(blobId, actualSha, ciphertext.Length, expiresAt);
            return Results.Text(JsonSerializer.Serialize(result, WireJson), "application/json");
        });

        app.MapGet("/circle/v1/brains/blobs/{blobId}", (HttpContext ctx, string blobId) =>
        {
            if (!TryAuthenticateBrain(ctx, out var callerBrainId))
            {
                return Results.Unauthorized();
            }

            // Uniform 404 for both missing blob and unauthorized caller brain!
            if (!_blobs.TryGetValue(blobId, out var stored) || !stored.Recipients.Contains(callerBrainId))
            {
                return Results.NotFound();
            }

            ctx.Response.Headers[BrainRelayTransportHosting.CircleBlobContentSha256Header] = stored.Sha256;
            return Results.File(stored.Ciphertext, "application/octet-stream");
        });

        // =====================================================================
        // 4. Paired PC Plane 2 Compute Relay (/circle/v1/compute/*)
        // =====================================================================
        app.MapPut(DeviceRoutes.Models, () => Results.NoContent());
        app.MapGet(DeviceRoutes.Next, () => Results.NoContent());

        app.MapPut(DeviceRoutes.ComputeCapability, async (HttpContext ctx) =>
        {
            var brainId = ResolveComputeBrainId(ctx);
            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var adv = JsonSerializer.Deserialize(raw, DeviceWireJson.Default.ComputeCapabilityAdvertisement);
            if (adv is null || string.IsNullOrWhiteSpace(adv.MachineId))
            {
                return Results.BadRequest();
            }

            _capabilities[(brainId, adv.MachineId)] = adv;
            return Results.NoContent();
        });

        app.MapGet(DeviceRoutes.ComputeNodes, (HttpContext ctx) =>
        {
            var brain = ctx.Request.Query["brain"].ToString();
            if (string.IsNullOrWhiteSpace(brain))
            {
                brain = ResolveComputeBrainId(ctx);
            }

            var nodes = _capabilities
                .Where(kv => string.Equals(kv.Key.BrainId, brain, StringComparison.Ordinal))
                .Select(static kv => kv.Value)
                .ToArray();

            var payload = JsonSerializer.Serialize(
                new BrainComputeNodesResponse(brain, nodes),
                DeviceWireJson.Default.BrainComputeNodesResponse);
            return Results.Text(payload, "application/json");
        });

        app.MapGet(DeviceRoutes.EngineeringNext, (HttpContext ctx) =>
        {
            var brainId = ResolveComputeBrainId(ctx);
            var queue = _engineeringQueues.GetOrAdd(brainId, static _ => Channel.CreateUnbounded<ConnectedEngineeringRequest>());
            if (queue.Reader.TryRead(out var job))
            {
                var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.ConnectedEngineeringRequest);
                return Results.Text(json, "application/json");
            }

            return Results.NoContent();
        });

        app.MapPost(DeviceRoutes.EngineeringDispatch, async ctx =>
        {
            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var dispatch = JsonSerializer.Deserialize(raw, DeviceWireJson.Default.BrainEngineeringDispatchRequest);
            if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.Brain) || string.IsNullOrWhiteSpace(dispatch.MachineId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Fail-closed 403 unless AllowConnectedEngineering == true AND "IEngineerRuns" capability is advertised.
            if (!_capabilities.TryGetValue((dispatch.Brain, dispatch.MachineId), out var adv)
                || !ClientComputeConsentPolicy.IsEligibleForConnectedEngineering(adv.AllowConnectedEngineering, adv.AdvertisedCapabilities))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var pending = new PendingEngineeringJob();
            _pendingEngineeringJobs[dispatch.Request.JobId] = pending;

            var queue = _engineeringQueues.GetOrAdd(dispatch.Brain, static _ => Channel.CreateUnbounded<ConnectedEngineeringRequest>());
            await queue.Writer.WriteAsync(dispatch.Request, ctx.RequestAborted).ConfigureAwait(false);

            // Stream NDJSON chunks back to CloudBrainRelayClient as the Paired PC uploads them.
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await ctx.Response.StartAsync(ctx.RequestAborted).ConfigureAwait(false);

            await foreach (var chunk in pending.Chunks.Reader.ReadAllAsync(ctx.RequestAborted).ConfigureAwait(false))
            {
                var outgoing = chunk;
                if (outgoing.Result is not null && EffectiveMutator is { } mutator)
                {
                    outgoing = outgoing with { Result = mutator(outgoing.Result) };
                }

                var line = JsonSerializer.Serialize(outgoing, DeviceWireJson.Default.EngineeringStreamChunk) + "\n";
                await ctx.Response.WriteAsync(line, Encoding.UTF8, ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }

            _pendingEngineeringJobs.TryRemove(dispatch.Request.JobId, out _);
        });

        app.MapPost(CircleRoutes.Prefix + "/compute/engineering/{jobId}/stream", async (HttpContext ctx, string jobId) =>
        {
            var chunkQueue = CapturedEngineeringStreamChunks.GetOrAdd(jobId, static _ => new ConcurrentQueue<EngineeringStreamChunk>());
            _pendingEngineeringJobs.TryGetValue(jobId, out var pending);

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            while (await reader.ReadLineAsync(ctx.RequestAborted).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var chunk = JsonSerializer.Deserialize(line, DeviceWireJson.Default.EngineeringStreamChunk);
                if (chunk is null)
                {
                    continue;
                }

                chunkQueue.Enqueue(chunk);
                if (chunk.Result is not null)
                {
                    CapturedEngineeringResults[jobId] = chunk.Result;
                }

                if (pending is not null)
                {
                    await pending.Chunks.Writer.WriteAsync(chunk, ctx.RequestAborted).ConfigureAwait(false);
                }
            }

            pending?.Chunks.Writer.TryComplete();
            return Results.NoContent();
        });

        app.MapPost(CircleRoutes.Prefix + "/compute/engineering/{jobId}/result", async (HttpContext ctx, string jobId) =>
        {
            var raw = await ReadBodyTextAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
            CapturedHttpBodies.Enqueue(raw);

            var result = JsonSerializer.Deserialize(raw, DeviceWireJson.Default.ConnectedEngineeringResponse);
            if (result is not null)
            {
                CapturedEngineeringResults[jobId] = result;
                if (_pendingEngineeringJobs.TryGetValue(jobId, out var pending))
                {
                    await pending.Chunks.Writer.WriteAsync(
                        new EngineeringStreamChunk(Stage: result.Stage, Done: true, Result: result),
                        ctx.RequestAborted).ConfigureAwait(false);
                    pending.Chunks.Writer.TryComplete();
                }
            }

            return Results.NoContent();
        });
    }

    private async Task HandleBrainWebSocketAsync(WebSocket ws, string brainId, CancellationToken ct)
    {
        var sendLock = new SemaphoreSlim(1, 1);

        async Task SendFrameAsync(BrainStreamFrame frame, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(frame, WireJson);
            CapturedWebSocketFrames.Enqueue(json);
            var bytes = Encoding.UTF8.GetBytes(json);
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }

        // Send initial auth-ok frame.
        await SendFrameAsync(
            new BrainStreamFrame(BrainStreamFrameTypes.AuthOk, ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)),
            ct).ConfigureAwait(false);

        var inbox = _inboxes.GetOrAdd(brainId, static _ => Channel.CreateUnbounded<BrainEnvelope>());
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Background pump: deliver queued envelopes over this WebSocket.
        var pushTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var env in inbox.Reader.ReadAllAsync(sessionCts.Token).ConfigureAwait(false))
                {
                    var leaseId = "ws-lease-" + Guid.NewGuid().ToString("N")[..12];
                    await SendFrameAsync(
                        new BrainStreamFrame(
                            BrainStreamFrameTypes.Deliver,
                            Envelope: env,
                            LeaseId: leaseId,
                            LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2)),
                        sessionCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal session shutdown.
            }
            catch (WebSocketException)
            {
                // Peer disconnected.
            }
        }, CancellationToken.None);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (ws.State == WebSocketState.CloseReceived)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct).ConfigureAwait(false);
                        }

                        return;
                    }

                    if (result.Count > 0)
                    {
                        ms.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0)
                {
                    continue;
                }

                var frameJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                CapturedWebSocketFrames.Enqueue(frameJson);
                var frame = JsonSerializer.Deserialize<BrainStreamFrame>(frameJson, WireJson);
                if (frame is null)
                {
                    continue;
                }

                switch (frame.Type)
                {
                    case BrainStreamFrameTypes.Reply when frame.Envelope is { } replyEnv:
                        CapturedEnvelopes.Enqueue(replyEnv);
                        if (!string.IsNullOrWhiteSpace(replyEnv.ReplyToMessageId))
                        {
                            var tcs = _replies.GetOrAdd(
                                replyEnv.ReplyToMessageId,
                                static _ => new TaskCompletionSource<BrainEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously));
                            tcs.TrySetResult(replyEnv);
                        }

                        await SendFrameAsync(
                            new BrainStreamFrame(BrainStreamFrameTypes.Acked, MessageId: replyEnv.ReplyToMessageId, LeaseId: frame.LeaseId),
                            ct).ConfigureAwait(false);
                        break;

                    case BrainStreamFrameTypes.Ack when !string.IsNullOrWhiteSpace(frame.MessageId):
                        _ackedMessages[frame.MessageId] = 1;
                        await SendFrameAsync(
                            new BrainStreamFrame(BrainStreamFrameTypes.Acked, MessageId: frame.MessageId, LeaseId: frame.LeaseId),
                            ct).ConfigureAwait(false);
                        break;

                    case BrainStreamFrameTypes.Ping:
                        await SendFrameAsync(new BrainStreamFrame(BrainStreamFrameTypes.Pong), ct).ConfigureAwait(false);
                        break;

                    case BrainStreamFrameTypes.AuthRefresh:
                        await SendFrameAsync(
                            new BrainStreamFrame(BrainStreamFrameTypes.AuthOk, ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)),
                            ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (WebSocketException)
        {
            // Client closed.
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            await pushTask.ConfigureAwait(false);
            sendLock.Dispose();
        }
    }

    private static bool TryAuthenticateBrain(HttpContext ctx, out string brainId)
    {
        var explicitHeader = ctx.Request.Headers[BrainIdHeader].ToString();
        if (!string.IsNullOrWhiteSpace(explicitHeader))
        {
            brainId = explicitHeader.Trim();
            return true;
        }

        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer brain:", StringComparison.OrdinalIgnoreCase))
        {
            brainId = auth["Bearer brain:".Length..].Trim();
            return !string.IsNullOrWhiteSpace(brainId);
        }

        brainId = string.Empty;
        return false;
    }

    private static string ResolveComputeBrainId(HttpContext ctx)
    {
        if (TryAuthenticateBrain(ctx, out var brainId))
        {
            return brainId;
        }

        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            var dot = token.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 ? token[..dot] : token;
        }

        return "owner";
    }

    private static async Task<string> ReadBodyTextAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record StoredBlob(byte[] Ciphertext, string Sha256, HashSet<string> Recipients, DateTimeOffset ExpiresAt);

    internal sealed record StoredBlobRecord(string BlobId, string[] Recipients, string Sha256, byte[] Ciphertext);

    private sealed class PendingEngineeringJob
    {
        public Channel<EngineeringStreamChunk> Chunks { get; } = Channel.CreateUnbounded<EngineeringStreamChunk>();
    }
}
