using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using DigitalBrain;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Versioned metadata for a live workspace artifact shared between Ino (MCP) and WASM studios.</summary>
internal sealed record ArtifactMetadata(
    string Path,
    string MimeType,
    int Version,
    string Sha256,
    long SizeBytes,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

[Signal("os.artifact.changed")]
internal sealed record ArtifactChanged(
    string Path,
    int Version,
    string Sha256,
    string UpdatedBy,
    DateTimeOffset UpdatedAt) : Signal;

/// <summary>
/// Versioned artifact store + live SSE fan-out hub enabling Ino (via MCP/tools)
/// and interactive WASM studios to share the exact same live document state.
/// </summary>
internal sealed class ArtifactLiveHub
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, ArtifactMetadata> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ArtifactChanged>>> _subscribers = new(StringComparer.OrdinalIgnoreCase);

    public ArtifactLiveHub(string? rootDirectory = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _rootDirectory = !string.IsNullOrWhiteSpace(rootDirectory)
            ? rootDirectory
            : !string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(localAppData, "IntoChat.OS", "artifacts")
                : Path.Combine(Path.GetTempPath(), "intochat-artifacts");
    }

    public static string NormalizePath(string rawPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        var normalized = rawPath.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/')) { normalized = "/" + normalized; }
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact path must not contain '..'.", nameof(rawPath));
        }
        return normalized;
    }

    public ArtifactMetadata? TryGetMetadata(string rawPath)
    {
        var key = NormalizePath(rawPath);
        return _meta.TryGetValue(key, out var meta) ? meta : null;
    }

    public async Task<(ArtifactMetadata Meta, byte[] Bytes)?> ReadAsync(string rawPath, CancellationToken ct = default)
    {
        var key = NormalizePath(rawPath);
        if (!_meta.TryGetValue(key, out var meta)) { return null; }
        var diskPath = ResolveDiskPath(key);
        if (!File.Exists(diskPath)) { return null; }
        var bytes = await File.ReadAllBytesAsync(diskPath, ct).ConfigureAwait(false);
        return (meta, bytes);
    }

    public async Task<ArtifactMetadata> WriteAsync(
        string rawPath,
        byte[] bytes,
        string actor = "ino",
        string? mimeType = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var key = NormalizePath(rawPath);
        var diskPath = ResolveDiskPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        await File.WriteAllBytesAsync(diskPath, bytes, ct).ConfigureAwait(false);

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var resolvedMime = string.IsNullOrWhiteSpace(mimeType) ? InferMimeType(key) : mimeType.Trim();
        var updatedBy = string.IsNullOrWhiteSpace(actor) ? "ino" : actor.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        var next = _meta.AddOrUpdate(
            key,
            _ => new ArtifactMetadata(key, resolvedMime, 1, hash, bytes.LongLength, updatedBy, now),
            (_, existing) => new ArtifactMetadata(key, resolvedMime, existing.Version + 1, hash, bytes.LongLength, updatedBy, now));

        var changed = new ArtifactChanged(next.Path, next.Version, next.Sha256, next.UpdatedBy, next.UpdatedAt);
        if (_subscribers.TryGetValue(key, out var channels))
        {
            foreach (var ch in channels.Values)
            {
                ch.Writer.TryWrite(changed);
            }
        }

        return next;
    }

    public (Guid SubscriptionId, ChannelReader<ArtifactChanged> Reader) Subscribe(string rawPath)
    {
        var key = NormalizePath(rawPath);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ArtifactChanged>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var bucket = _subscribers.GetOrAdd(key, static _ => new ConcurrentDictionary<Guid, Channel<ArtifactChanged>>());
        bucket[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(string rawPath, Guid subscriptionId)
    {
        var key = NormalizePath(rawPath);
        if (_subscribers.TryGetValue(key, out var bucket) && bucket.TryRemove(subscriptionId, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    private string ResolveDiskPath(string normalizedKey)
    {
        var relative = normalizedKey.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(_rootDirectory);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved artifact path escaped root directory.");
        }
        return candidate;
    }

    private static string InferMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".psd" => "image/vnd.adobe.photoshop",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".mp4" => "video/mp4",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };
}
