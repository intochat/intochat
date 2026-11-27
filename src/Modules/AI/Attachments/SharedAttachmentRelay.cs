using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Invocation;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Attachments;

/// <summary>
/// End-to-end encrypted attachment &amp; <c>VoiceClipCard</c> audio relay for <see cref="ThreadTrustPosture.CrossBrainShared"/>
/// conversations over <c>/circle/v1/brains/blobs</c> (<c>X-IntoChat-Recipients</c> + <c>X-IntoChat-Content-Sha256</c>).
/// Seals plaintext payloads with <see cref="BrainBlobSealer"/> before upload and verifies sender P-256 ECDSA signature,
/// ciphertext SHA-256, plaintext SHA-256, and attachment safety rules before exposing decrypted bytes.
/// </summary>
internal sealed class SharedAttachmentRelay(IServiceProvider services)
{
    private IBrainRelayClient? TryGetRelayClient()
        => services.GetService<IBrainRelayClientProvider>()?.TryGetClient()
            ?? services.GetService<IBrainRelayClient>();

    public bool IsAvailable
        => TryGetRelayClient() is not null
            && services.GetService<IBrainKeySigner>() is not null;

    /// <summary>
    /// Reads <paramref name="localAttachment"/> from <paramref name="senderBrain"/>'s local <see cref="FileAttachmentStore"/>,
    /// seals it with <see cref="BrainBlobSealer"/> for all <paramref name="recipients"/>, and uploads opaque ciphertext
    /// via <see cref="IBrainRelayClient.UploadBlobAsync"/>.
    /// </summary>
    public async Task<AttachmentRef> SealAndUploadAsync(
        BrainId senderBrain,
        AttachmentRef localAttachment,
        IReadOnlyList<(BrainId BrainId, BrainPublicKeys PublicKeys)> recipients,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localAttachment);
        ArgumentNullException.ThrowIfNull(recipients);

        var store = services.GetService<FileAttachmentStore>()
            ?? throw new InvalidOperationException("Attachment store is unavailable; cannot seal attachment.");
        var relay = TryGetRelayClient()
            ?? throw new InvalidOperationException("Brain relay client is not configured; cannot upload shared E2EE blob.");
        var signer = services.GetService<IBrainKeySigner>()
            ?? throw new InvalidOperationException("Brain key signer is unavailable; cannot sign shared E2EE blob.");

        var stored = await store.TryReadAsync(senderBrain, localAttachment.Id, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException($"Attachment '{localAttachment.Id}' is not accessible from brain '{senderBrain.Value}'.");

        if (recipients.Count == 0 || recipients.Count > BrainBlobLimits.MaxRecipients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipients),
                recipients.Count,
                $"Shared attachment relay requires between 1 and {BrainBlobLimits.MaxRecipients} recipients.");
        }

        var recipientBrainIds = new BrainId[recipients.Count];
        for (var i = 0; i < recipients.Count; i++)
        {
            BrainPublicKeyValidation.ValidateNistP256(recipients[i].PublicKeys);
            recipientBrainIds[i] = recipients[i].BrainId;
        }

        var sealedBytes = await BrainBlobSealer.SealAsync(
            senderBrain,
            signer,
            stored.Bytes,
            recipients,
            ct).ConfigureAwait(false);

        var uploaded = await relay.UploadBlobAsync(sealedBytes, recipientBrainIds, ct).ConfigureAwait(false);
        return localAttachment with
        {
            SizeBytes = stored.Metadata.SizeBytes,
            Sha256 = stored.Metadata.Sha256,
            BlobId = uploaded.BlobId,
            BlobSha256 = uploaded.Sha256,
            SenderBrainId = senderBrain.Value,
        };
    }

    /// <summary>
    /// Downloads an opaque E2EE blob referenced by <paramref name="remoteAttachment"/> from <c>/circle/v1/brains/blobs</c>,
    /// verifies the sender's P-256 ECDSA signature and SHA-256 digests fail-closed via <see cref="BrainBlobSealer.UnsealAsync"/>,
    /// validates attachment content/magic bytes, and optionally persists the verified plaintext in <paramref name="recipientBrain"/>'s
    /// local <see cref="FileAttachmentStore"/> so <c>VoiceClipCard</c> and image previews can play/render it directly.
    /// </summary>
    public async Task<(AttachmentRef Attachment, byte[] Plaintext)> DownloadAndUnsealAsync(
        BrainId recipientBrain,
        AttachmentRef remoteAttachment,
        BrainPublicKeys? expectedSenderKeys = null,
        string? redactedTranscript = null,
        bool persistToLocalStore = true,
        ConversationId? conversationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteAttachment);

        if (!BrainBlobLimits.IsValidBlobId(remoteAttachment.BlobId)
            || !FriendCodeBundleSigner.IsLowercaseSha256Hex(remoteAttachment.BlobSha256)
            || string.IsNullOrWhiteSpace(remoteAttachment.SenderBrainId))
        {
            throw new UnauthorizedAccessException("Shared attachment is missing verified E2EE blob metadata.");
        }

        var relay = TryGetRelayClient()
            ?? throw new InvalidOperationException("Brain relay client is not configured; cannot download shared E2EE blob.");
        var signer = services.GetService<IBrainKeySigner>()
            ?? throw new InvalidOperationException("Brain key signer is unavailable; cannot unseal shared E2EE blob.");

        var senderBrain = new BrainId(remoteAttachment.SenderBrainId.Trim());
        var senderKeys = expectedSenderKeys;
        if (senderKeys is null)
        {
            if (senderBrain == recipientBrain)
            {
                senderKeys = await signer.GetPublicKeysAsync(recipientBrain, ct).ConfigureAwait(false);
            }
            else if (services.GetService<IBrainKeyDirectory>() is { } directory)
            {
                senderKeys = await directory.GetPinnedKeysForAsync(recipientBrain, senderBrain, ct).ConfigureAwait(false);
            }
        }

        if (senderKeys is null)
        {
            throw new CryptographicException($"No pinned public keys are known for blob sender brain '{senderBrain.Value}'.");
        }

        BrainPublicKeyValidation.ValidateNistP256(senderKeys);

        var downloaded = await relay.DownloadBlobAsync(
            remoteAttachment.BlobId!,
            expectedSha256: remoteAttachment.BlobSha256,
            ct).ConfigureAwait(false);
        if (downloaded is null)
        {
            throw new UnauthorizedAccessException($"Shared E2EE blob '{remoteAttachment.BlobId}' was not found or is unauthorized.");
        }

        var plaintext = await BrainBlobSealer.UnsealAsync(
            downloaded.Ciphertext,
            recipientBrain,
            signer,
            senderKeys,
            ct).ConfigureAwait(false);

        var isVoiceNote = string.Equals(remoteAttachment.Kind, AttachmentValidator.KindVoiceNote, StringComparison.Ordinal);
        var validated = AttachmentValidator.ValidateUpload(
            remoteAttachment.FileName,
            remoteAttachment.MediaType,
            plaintext,
            requireVoiceNote: isVoiceNote,
            durationMs: remoteAttachment.DurationMs);

        if (!string.IsNullOrWhiteSpace(remoteAttachment.Sha256)
            && !string.Equals(validated.Sha256, remoteAttachment.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Decrypted shared attachment SHA-256 digest does not match envelope metadata.");
        }

        if (persistToLocalStore && services.GetService<FileAttachmentStore>() is { } store)
        {
            var saved = await store.SaveAsync(
                recipientBrain,
                conversationId,
                validated,
                plaintext,
                redactedTranscript,
                ct).ConfigureAwait(false);

            return (
                saved with
                {
                    BlobId = remoteAttachment.BlobId,
                    BlobSha256 = downloaded.Sha256,
                    SenderBrainId = senderBrain.Value,
                },
                plaintext);
        }

        var materialized = remoteAttachment with
        {
            Kind = validated.Kind,
            FileName = validated.FileName,
            MediaType = validated.MediaType,
            SizeBytes = validated.SizeBytes,
            Sha256 = validated.Sha256,
            DurationMs = validated.DurationMs,
            Transcript = redactedTranscript,
            BlobSha256 = downloaded.Sha256,
            SenderBrainId = senderBrain.Value,
        };
        return (materialized, plaintext);
    }
}
