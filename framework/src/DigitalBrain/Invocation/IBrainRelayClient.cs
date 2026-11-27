using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.Invocation;

/// <summary>
/// Constants and validation helpers for E2EE media blob relay (<c>/circle/v1/brains/blobs</c>).
/// </summary>
public static class BrainBlobLimits
{
    /// <summary>Maximum allowed ciphertext blob size (25 MiB).</summary>
    public const long MaxBodyBytes = 25L * 1024 * 1024;

    /// <summary>Maximum number of recipient brains allowed in a single blob upload.</summary>
    public const int MaxRecipients = 64;

    /// <summary>Maximum character length of a single recipient brain identifier.</summary>
    public const int MaxRecipientBrainIdLength = 256;

    /// <summary>Maximum character length of a blob identifier.</summary>
    public const int MaxBlobIdLength = 128;

    /// <summary>Validates that <paramref name="blobId"/> is a non-empty URL-safe blob identifier within bounds.</summary>
    public static bool IsValidBlobId(string? blobId)
    {
        if (string.IsNullOrEmpty(blobId) || blobId.Length > MaxBlobIdLength)
        {
            return false;
        }

        foreach (var c in blobId)
        {
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Response returned by <c>POST /circle/v1/brains/blobs</c>.</summary>
public sealed record BrainBlobUploadResult(
    string BlobId,
    string Sha256,
    long SizeBytes,
    DateTimeOffset ExpiresAt);

/// <summary>Downloaded ciphertext payload and verified SHA-256 digest from <c>GET /circle/v1/brains/blobs/{blobId}</c>.</summary>
public sealed record BrainBlobDownloadResult(
    byte[] Ciphertext,
    string Sha256);

/// <summary>Wire frame exchanged over the <c>GET /circle/v1/brains/stream</c> WebSocket relay.</summary>
public sealed record BrainStreamFrame(
    string Type,
    BrainEnvelope? Envelope = null,
    string? LeaseId = null,
    DateTimeOffset? LeaseExpiresAt = null,
    string? MessageId = null,
    string? AccessToken = null,
    string? MachineProof = null,
    DateTimeOffset? ExpiresAt = null,
    string? Code = null,
    string? Message = null,
    string? ServiceAssertion = null);

/// <summary>Canonical frame type discriminators for <see cref="BrainStreamFrame"/>.</summary>
public static class BrainStreamFrameTypes
{
    public const string Send = "send";
    public const string Reply = "reply";
    public const string Ack = "ack";
    public const string AuthRefresh = "auth-refresh";
    public const string Ping = "ping";
    public const string Deliver = "deliver";
    public const string Acked = "acked";
    public const string AuthOk = "auth-ok";
    public const string Pong = "pong";
    public const string Error = "error";
}

/// <summary>
/// Configuration and transport hooks for <see cref="HttpBrainRelayClient"/>,
/// including WebSocket stream tuning, header names, and test seams.
/// </summary>
public sealed class BrainRelayClientOptions
{
    /// <summary>Header carrying comma-separated recipient brain IDs on <c>POST /circle/v1/brains/blobs</c>.</summary>
    public string RecipientsHeaderName { get; set; } = "X-Brain-Recipients";

    /// <summary>Header carrying the lowercase hex SHA-256 digest of blob ciphertext.</summary>
    public string ContentSha256HeaderName { get; set; } = "X-Brain-Content-Sha256";

    /// <summary>Initial reconnect backoff delay after a WebSocket close or transient failure.</summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum reconnect backoff delay.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Duration to fall back to HTTP polling when a proxy rejects WebSocket upgrade.</summary>
    public TimeSpan HttpFallbackDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>WebSocket protocol keep-alive interval.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Maximum inbound WebSocket text frame size in bytes (guards against oversized frames).</summary>
    public int MaxInboundFrameBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Proportional jitter factor in [0, 0.5] applied to reconnect backoff delays.</summary>
    public double JitterFactor { get; set; } = 0.25;

    /// <summary>
    /// Optional async delegate that populates HTTP upgrade headers for WebSocket <c>GET /circle/v1/brains/stream</c>.
    /// Receives the HTTP/HTTPS stream URI so machine proofs and service assertions bind to the HTTP request URL.
    /// Authentication is always sent via headers and never via query strings.
    /// </summary>
    public Func<Uri, IDictionary<string, string>, CancellationToken, ValueTask>? AuthorizeWebSocketAsync { get; set; }

    /// <summary>
    /// Optional async delegate that creates an <see cref="BrainStreamFrameTypes.AuthRefresh"/> frame for an active stream.
    /// </summary>
    public Func<Uri, CancellationToken, ValueTask<BrainStreamFrame?>>? CreateAuthRefreshFrameAsync { get; set; }

    /// <summary>
    /// Optional custom WebSocket connector (primarily for deterministic unit testing).
    /// Receives the <c>ws://</c>/<c>wss://</c> URI and the upgrade request headers.
    /// </summary>
    public Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>>? WebSocketConnectAsync { get; set; }

    /// <summary>Optional deterministic RNG returning a value in [0, 1) for jitter calculation in tests.</summary>
    public Func<double>? NextRandomDouble { get; set; }

    /// <summary>Optional diagnostic callback invoked on notable stream lifecycle events (never receives secrets).</summary>
    public Action<string>? OnStreamDiagnostic { get; set; }
}

/// <summary>
/// Client abstraction for cross-brain sealed relay transport, WebSocket push streaming,
/// friend-code rendezvous (<c>/circle/v1/friends/codes/*</c>), and E2EE blob relay (<c>/circle/v1/brains/blobs</c>).
/// </summary>
public interface IBrainRelayClient
{
    /// <summary>Posts a one-way sealed <paramref name="envelope"/> to the relay.</summary>
    Task SendOneWayAsync(BrainEnvelope envelope, CancellationToken ct = default);

    /// <summary>Posts <paramref name="envelope"/> to the relay and waits for the matching reply envelope.</summary>
    Task<BrainEnvelope> SendAndWaitReplyAsync(BrainEnvelope envelope, CancellationToken ct = default);

    /// <summary>Explicitly acknowledges a leased envelope via <c>POST /circle/v1/brains/ack/{messageId}</c>.</summary>
    Task AckAsync(string messageId, string? leaseId = null, CancellationToken ct = default);

    /// <summary>Polls the relay once for the next incoming envelope and delivers it to <paramref name="receiver"/>.</summary>
    Task<bool> PollOnceAsync(BrainEnvelopeReceiver receiver, CancellationToken ct = default);

    /// <summary>Continuously polls the relay over HTTP until <paramref name="ct"/> is cancelled.</summary>
    Task RunPollLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct);

    /// <summary>
    /// Connects to <c>GET /circle/v1/brains/stream</c> over WebSocket with header-only authentication,
    /// durable lease-ack after local delivery, backoff on close codes <c>1008</c>/<c>1009</c>,
    /// and automatic HTTP polling fallback when WebSocket upgrade is rejected.
    /// </summary>
    Task RunStreamLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct);

    /// <summary>Publishes a self-signed <see cref="FriendCodeBundle"/> to <c>POST /circle/v1/friends/codes</c>.</summary>
    Task PublishFriendCodeAsync(FriendCodeBundle bundle, CancellationToken ct = default);

    /// <summary>Resolves and verifies a <see cref="FriendCodeBundle"/> via <c>POST /circle/v1/friends/codes/resolve</c>.</summary>
    Task<FriendCodeBundle?> ResolveFriendCodeAsync(string codeHash, CancellationToken ct = default);

    /// <summary>Revokes a published friend code via <c>DELETE /circle/v1/friends/codes/{codeHash}</c>.</summary>
    Task RevokeFriendCodeAsync(string codeHash, CancellationToken ct = default);

    /// <summary>Uploads opaque E2EE ciphertext bytes to <c>POST /circle/v1/brains/blobs</c> for <paramref name="recipients"/>.</summary>
    Task<BrainBlobUploadResult> UploadBlobAsync(
        ReadOnlyMemory<byte> ciphertext,
        IReadOnlyList<BrainId> recipients,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads opaque E2EE ciphertext bytes from <c>GET /circle/v1/brains/blobs/{blobId}</c>.
    /// Returns <see langword="null"/> on uniform <c>404 Not Found</c>.
    /// </summary>
    Task<BrainBlobDownloadResult?> DownloadBlobAsync(
        string blobId,
        string? expectedSha256 = null,
        CancellationToken ct = default);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrainStreamFrame))]
[JsonSerializable(typeof(BrainBlobUploadResult))]
internal sealed partial class BrainStreamJsonContext : JsonSerializerContext;
