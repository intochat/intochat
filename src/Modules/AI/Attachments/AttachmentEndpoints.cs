using System.Globalization;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Net.Http.Headers;

namespace IntoChat.OS.Modules.AI.Attachments;

internal static class AttachmentEndpoints
{
    internal const long MaxUploadRequestBytes = AttachmentValidator.MaxFileAttachmentBytes + 64 * 1024;

    public static async Task<IResult> UploadAsync(
        HttpContext http,
        FileAttachmentStore store,
        IVoiceClients voiceClients,
        IAudioConverter? audioConverter,
        ISecretRedactor? redactor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);

        if (AIModule.StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        byte[] bytes;
        string? fileName;
        string? declaredMediaType;
        bool isVoiceNote;
        bool shouldTranscribe;
        int? durationMs;
        ConversationId? conversationId = null;

        try
        {
            (bytes, fileName, declaredMediaType, isVoiceNote, shouldTranscribe, durationMs, conversationId) =
                await ReadUploadRequestAsync(http.Request, ct).ConfigureAwait(false);
        }
        catch (AttachmentValidationException error)
        {
            return Results.Problem(error.Message, statusCode: error.StatusCode);
        }
        catch (InvalidDataException error)
        {
            var isTooLarge = error.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase);
            return Results.Problem(
                error.Message,
                statusCode: isTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }

        ValidatedAttachment validated;
        try
        {
            validated = AttachmentValidator.ValidateUpload(fileName, declaredMediaType, bytes, isVoiceNote, durationMs);
        }
        catch (AttachmentValidationException error)
        {
            return Results.Problem(error.Message, statusCode: error.StatusCode);
        }

        string? redactedTranscript = null;
        if (string.Equals(validated.Kind, AttachmentValidator.KindVoiceNote, StringComparison.Ordinal) && shouldTranscribe)
        {
            var rawTranscript = await TryTranscribeVoiceNoteAsync(
                caller.Brain.Value,
                validated,
                bytes,
                voiceClients,
                audioConverter,
                ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rawTranscript))
            {
                // Fail closed: never persist or expose an unredacted voice-note transcript.
                if (redactor is null)
                {
                    return Results.Problem(
                        "Secret redactor is unavailable; voice-note transcript cannot be stored safely.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                redactedTranscript = await redactor.Redact(rawTranscript, ct).ConfigureAwait(false);
            }
        }

        var saved = await store.SaveAsync(
            caller.Brain,
            conversationId,
            validated,
            bytes,
            redactedTranscript,
            ct).ConfigureAwait(false);

        return Results.Json(saved, AttachmentJsonContext.Default.AttachmentRef);
    }

    public static async Task<IResult> DownloadAsync(
        HttpContext http,
        string id,
        FileAttachmentStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);

        if (AIModule.StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!AttachmentValidator.IsValidAttachmentId(id))
        {
            return Results.Problem("Invalid attachment id.", statusCode: StatusCodes.Status400BadRequest);
        }

        var stored = await store.TryReadAsync(caller.Brain, id, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        http.Response.Headers.CacheControl = "private, max-age=3600";

        var inline = stored.Metadata.Kind is AttachmentValidator.KindImage or AttachmentValidator.KindVoiceNote;
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(stored.Metadata.FileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();

        return Results.File(stored.Bytes, stored.Metadata.MediaType);
    }

    public static async Task<IResult> DownloadSharedBlobAsync(
        HttpContext http,
        string blobId,
        SharedAttachmentRelay sharedRelay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(sharedRelay);

        if (AIModule.StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var senderBrainId = http.Request.Query["sender"].ToString().Trim();
        var blobSha256 = http.Request.Query["sha256"].ToString().Trim();
        var fileName = http.Request.Query["fileName"].ToString().Trim();
        var mediaType = http.Request.Query["mediaType"].ToString().Trim();
        var kind = http.Request.Query["kind"].ToString().Trim();
        var plaintextSha256 = http.Request.Query["contentSha256"].ToString().Trim();

        if (!sharedRelay.IsAvailable)
        {
            return Results.NotFound();
        }

        var reference = new IntoChat.OS.Modules.Flutter.Conversations.AttachmentRef(
            Id: Guid.NewGuid().ToString("N"),
            Kind: string.IsNullOrWhiteSpace(kind) ? AttachmentValidator.KindFile : kind,
            FileName: fileName,
            MediaType: mediaType,
            SizeBytes: 1,
            Url: $"/ai/attachments/blob/{blobId}",
            Sha256: string.IsNullOrWhiteSpace(plaintextSha256) ? null : plaintextSha256,
            BlobId: blobId,
            BlobSha256: blobSha256,
            SenderBrainId: senderBrainId);

        try
        {
            var (materialized, plaintext) = await sharedRelay.DownloadAndUnsealAsync(
                caller.Brain,
                reference,
                persistToLocalStore: false,
                ct: ct).ConfigureAwait(false);

            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            http.Response.Headers.CacheControl = "private, max-age=3600";

            var inline = materialized.Kind is AttachmentValidator.KindImage or AttachmentValidator.KindVoiceNote;
            var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
            disposition.SetHttpFileName(materialized.FileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();

            return Results.File(plaintext, materialized.MediaType);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Results.NotFound();
        }
        catch (AttachmentValidationException error)
        {
            return Results.Problem(error.Message, statusCode: error.StatusCode);
        }
    }

    private static async Task<(
        byte[] Bytes,
        string? FileName,
        string? MediaType,
        bool IsVoiceNote,
        bool ShouldTranscribe,
        int? DurationMs,
        ConversationId? ConversationId)> ReadUploadRequestAsync(HttpRequest request, CancellationToken ct)
    {
        var rawKind = request.Query["kind"].ToString();
        var rawTranscribe = request.Query["transcribe"].ToString();
        var rawDuration = request.Query["durationMs"].ToString();
        var rawConversation = request.Query["conversation"].ToString();

        byte[] bytes;
        string? fileName;
        string? mediaType;

        if (request.HasFormContentType)
        {
            var formOptions = new FormOptions
            {
                MultipartBodyLengthLimit = MaxUploadRequestBytes,
                ValueCountLimit = 32,
            };
            var form = await request.ReadFormAsync(formOptions, ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                throw new AttachmentValidationException("Attachment file payload is required.", statusCode: 400);
            }

            if (file.Length > AttachmentValidator.MaxFileAttachmentBytes)
            {
                throw new AttachmentValidationException($"Attachment exceeds the {AttachmentValidator.MaxFileAttachmentBytes / (1024 * 1024)} MiB size limit.", statusCode: 413);
            }

            fileName = file.FileName;
            mediaType = file.ContentType;
            if (string.IsNullOrWhiteSpace(rawKind) && form.TryGetValue("kind", out var formKind)) { rawKind = formKind.ToString(); }
            if (string.IsNullOrWhiteSpace(rawTranscribe) && form.TryGetValue("transcribe", out var formTrans)) { rawTranscribe = formTrans.ToString(); }
            if (string.IsNullOrWhiteSpace(rawDuration) && form.TryGetValue("durationMs", out var formDur)) { rawDuration = formDur.ToString(); }
            if (string.IsNullOrWhiteSpace(rawConversation) && form.TryGetValue("conversation", out var formConv)) { rawConversation = formConv.ToString(); }

            await using var stream = file.OpenReadStream();
            bytes = await ReadBoundedAsync(stream, AttachmentValidator.MaxFileAttachmentBytes, ct).ConfigureAwait(false);
        }
        else
        {
            fileName = request.Query["fileName"].ToString();
            mediaType = request.ContentType;
            bytes = await ReadBoundedAsync(request.Body, AttachmentValidator.MaxFileAttachmentBytes, ct).ConfigureAwait(false);
        }

        var isVoiceNote = string.Equals(rawKind, AttachmentValidator.KindVoiceNote, StringComparison.OrdinalIgnoreCase);
        var shouldTranscribe = isVoiceNote
            ? !string.Equals(rawTranscribe, "false", StringComparison.OrdinalIgnoreCase)
            : string.Equals(rawTranscribe, "true", StringComparison.OrdinalIgnoreCase);

        int? durationMs = null;
        if (!string.IsNullOrWhiteSpace(rawDuration))
        {
            if (!int.TryParse(rawDuration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDuration))
            {
                throw new AttachmentValidationException("durationMs must be an integer.", statusCode: 400);
            }

            durationMs = parsedDuration;
        }

        ConversationId? conversationId = null;
        if (!string.IsNullOrWhiteSpace(rawConversation) && ConversationId.TryParse(rawConversation, out var parsedConv))
        {
            conversationId = parsedConv;
        }

        return (bytes, fileName, mediaType, isVoiceNote, shouldTranscribe, durationMs, conversationId);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new AttachmentValidationException($"Attachment exceeds the {maxBytes / (1024 * 1024)} MiB size limit.", statusCode: 413);
            }

            ms.Write(buffer, 0, read);
        }

        if (total == 0)
        {
            throw new AttachmentValidationException("Attachment payload must not be empty.", statusCode: 400);
        }

        return ms.ToArray();
    }

    private static async Task<string?> TryTranscribeVoiceNoteAsync(
        string brain,
        ValidatedAttachment validated,
        byte[] bytes,
        IVoiceClients voiceClients,
        IAudioConverter? audioConverter,
        CancellationToken ct)
    {
        string? tempInput = null;
        string? tempWav = null;
        try
        {
            var voice = await voiceClients.Hear(brain, ct).ConfigureAwait(false);
            if (voice.Client is null)
            {
                return null;
            }

            using var client = voice.Client;
            Stream audioStream;
            if (validated.MediaType is "audio/ogg" or "audio/opus" && audioConverter is not null)
            {
                var ext = Path.GetExtension(validated.FileName);
                if (string.IsNullOrWhiteSpace(ext)) { ext = ".ogg"; }
                tempInput = Path.Combine(Path.GetTempPath(), $"db_voicenote_{Guid.NewGuid():N}{ext}");
                await File.WriteAllBytesAsync(tempInput, bytes, ct).ConfigureAwait(false);
                tempWav = audioConverter.ConvertToWav(tempInput);
                audioStream = File.OpenRead(tempWav);
            }
            else
            {
                audioStream = new MemoryStream(bytes, writable: false);
            }

            await using (audioStream.ConfigureAwait(false))
            {
                var response = await client.GetTextAsync(
                    audioStream,
                    new SpeechToTextOptions { ModelId = voice.Model?.Value },
                    ct).ConfigureAwait(false);
                var text = response.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                return text.Length > AttachmentValidator.MaxTranscriptChars
                    ? text[..AttachmentValidator.MaxTranscriptChars]
                    : text;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Persist the voice clip cleanly with Transcript = null on transient STT/conversion failures.
            OsConsoleLog.For<FileAttachmentStore>().LogSwallowed(error, "auto-transcribing uploaded voice-note clip");
            return null;
        }
        finally
        {
            if (tempWav is not null && File.Exists(tempWav)) { File.Delete(tempWav); }
            if (tempInput is not null && File.Exists(tempInput)) { File.Delete(tempInput); }
        }
    }
}
