using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Brains;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.AI.Attachments;

internal sealed record StoredAttachmentMetadata(
    string Id,
    string OwnerBrain,
    string? ConversationId,
    string Kind,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    int? DurationMs,
    string? Transcript,
    DateTimeOffset CreatedAt);

internal sealed record StoredAttachment(StoredAttachmentMetadata Metadata, byte[] Bytes);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StoredAttachmentMetadata))]
[JsonSerializable(typeof(AttachmentRef))]
internal sealed partial class AttachmentJsonContext : JsonSerializerContext;

// Per-brain filesystem attachment store with strict canonical-path containment and ACL ownership checks.
internal sealed class FileAttachmentStore
{
    private const int MaxInlineVisionImageBytes = 4 * 1024 * 1024;
    private readonly string _rootDir;
    private readonly TimeProvider _clock;

    public FileAttachmentStore(IConfiguration configuration, TimeProvider? clock = null)
        : this(ResolveRootDirectory(configuration), clock)
    {
    }

    internal FileAttachmentStore(string rootDir, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        _rootDir = Path.GetFullPath(rootDir);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AttachmentRef> SaveAsync(
        BrainId ownerBrain,
        ConversationId? conversationId,
        ValidatedAttachment validated,
        byte[] bytes,
        string? redactedTranscript,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentNullException.ThrowIfNull(bytes);
        if (!BrainScope.IsValidId(ownerBrain.Value))
        {
            throw new UnauthorizedAccessException("Valid brain scope is required to store an attachment.");
        }

        var id = Guid.NewGuid().ToString("N");
        var brainDir = ResolveBrainDirectory(ownerBrain);
        Directory.CreateDirectory(brainDir);

        var (blobPath, metaPath) = ResolveItemPaths(brainDir, id);
        var metadata = new StoredAttachmentMetadata(
            id,
            ownerBrain.Value,
            conversationId?.Value,
            validated.Kind,
            validated.FileName,
            validated.MediaType,
            validated.SizeBytes,
            validated.Sha256,
            validated.DurationMs,
            redactedTranscript,
            _clock.GetUtcNow());

        await File.WriteAllBytesAsync(blobPath, bytes, ct).ConfigureAwait(false);
        var metaBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, AttachmentJsonContext.Default.StoredAttachmentMetadata);
        await File.WriteAllBytesAsync(metaPath, metaBytes, ct).ConfigureAwait(false);

        return new AttachmentRef(
            Id: id,
            Kind: metadata.Kind,
            FileName: metadata.FileName,
            MediaType: metadata.MediaType,
            SizeBytes: metadata.SizeBytes,
            Url: $"/ai/attachments/{id}",
            DurationMs: metadata.DurationMs,
            Transcript: metadata.Transcript,
            Sha256: metadata.Sha256);
    }

    public async Task<StoredAttachment?> TryReadAsync(BrainId callerBrain, string id, CancellationToken ct)
    {
        var metadata = await TryReadMetadataAsync(callerBrain, id, ct).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var brainDir = ResolveBrainDirectory(callerBrain);
        var (blobPath, _) = ResolveItemPaths(brainDir, id);
        if (!File.Exists(blobPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(blobPath, ct).ConfigureAwait(false);
        return new StoredAttachment(metadata, bytes);
    }

    public async Task<bool> OwnsAsync(BrainId callerBrain, string id, CancellationToken ct)
        => await TryReadMetadataAsync(callerBrain, id, ct).ConfigureAwait(false) is not null;

    public async Task<string?> TryReadImageDataUriAsync(BrainId callerBrain, string id, CancellationToken ct)
    {
        var stored = await TryReadAsync(callerBrain, id, ct).ConfigureAwait(false);
        if (stored is null
            || !string.Equals(stored.Metadata.Kind, AttachmentValidator.KindImage, StringComparison.Ordinal)
            || stored.Bytes.Length is 0 or > MaxInlineVisionImageBytes)
        {
            return null;
        }

        return $"data:{stored.Metadata.MediaType};base64,{Convert.ToBase64String(stored.Bytes)}";
    }

    private async Task<StoredAttachmentMetadata?> TryReadMetadataAsync(BrainId callerBrain, string id, CancellationToken ct)
    {
        if (!BrainScope.IsValidId(callerBrain.Value) || !AttachmentValidator.IsValidAttachmentId(id))
        {
            return null;
        }

        var brainDir = ResolveBrainDirectory(callerBrain);
        var (_, metaPath) = ResolveItemPaths(brainDir, id);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var metaBytes = await File.ReadAllBytesAsync(metaPath, ct).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize(metaBytes, AttachmentJsonContext.Default.StoredAttachmentMetadata);
        if (metadata is null || !string.Equals(metadata.OwnerBrain, callerBrain.Value, StringComparison.Ordinal))
        {
            return null;
        }

        return metadata;
    }

    private string ResolveBrainDirectory(BrainId brain)
    {
        if (!BrainScope.IsValidId(brain.Value))
        {
            throw new UnauthorizedAccessException("Invalid brain scope.");
        }

        var candidate = Path.GetFullPath(Path.Combine(_rootDir, brain.Value));
        EnsureContained(_rootDir, candidate);
        return candidate;
    }

    private static (string BlobPath, string MetaPath) ResolveItemPaths(string brainDir, string id)
    {
        if (!AttachmentValidator.IsValidAttachmentId(id))
        {
            throw new AttachmentValidationException("Invalid attachment id.");
        }

        var blobPath = Path.GetFullPath(Path.Combine(brainDir, id + ".bin"));
        var metaPath = Path.GetFullPath(Path.Combine(brainDir, id + ".meta.json"));
        EnsureContained(brainDir, blobPath);
        EnsureContained(brainDir, metaPath);
        return (blobPath, metaPath);
    }

    private static void EnsureContained(string root, string candidate)
    {
        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Attachment path escaped its brain storage boundary.");
        }
    }

    private static string ResolveRootDirectory(IConfiguration configuration)
    {
        var home = configuration[IntoChatOSNames.HomeKey];
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "attachments");
        }

        return Path.Combine(Path.GetTempPath(), "intochat-attachments");
    }
}
