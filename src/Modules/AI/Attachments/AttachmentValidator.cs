using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Attachments;

internal sealed record ValidatedAttachment(
    string Kind,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    int? DurationMs);

internal sealed class AttachmentValidationException(string message, int statusCode = 400) : ArgumentException(message)
{
    public int StatusCode { get; } = statusCode;
}

internal static class AttachmentValidator
{
    public const int MaxAudioAttachmentBytes = 10 * 1024 * 1024;      // 10 MiB max for audio/voice-note clips
    public const int MaxFileAttachmentBytes = 25 * 1024 * 1024;       // 25 MiB max for general file/image attachments
    public const int MaxAttachmentBytes = MaxFileAttachmentBytes;
    public const int MaxAttachmentsPerTurn = 6;                       // max attachments in one Say() turn
    public const long MaxTotalTurnAttachmentBytes = 50L * 1024 * 1024; // 50 MiB total per turn
    public const int MaxVoiceNoteDurationMs = 5 * 60 * 1000;          // 5 minutes max voice note
    public const int MaxFileNameLength = 160;
    public const int MaxTranscriptChars = 32_000;

    public static int MaxBytesForKind(string kind)
        => string.Equals(kind, KindVoiceNote, StringComparison.Ordinal)
            ? MaxAudioAttachmentBytes
            : MaxFileAttachmentBytes;

    public const string KindImage = "image";
    public const string KindFile = "file";
    public const string KindVoiceNote = "voice-note";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bat", ".cmd", ".ps1", ".psm1", ".sh", ".bash", ".zsh",
        ".js", ".mjs", ".cjs", ".ts", ".vbs", ".vbe", ".wsf", ".wsh", ".msi", ".msp", ".com", ".scr",
        ".pif", ".jar", ".class", ".html", ".htm", ".xhtml", ".svg", ".svgz", ".xml", ".xsl",
        ".php", ".py", ".rb", ".pl", ".hta", ".reg", ".lnk", ".app", ".dmg", ".pkg", ".deb", ".rpm",
    };

    private static readonly HashSet<string> BlockedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-dosexec", "application/x-executable",
        "application/x-sh", "application/x-csh", "application/x-bat", "application/x-msi",
        "application/javascript", "text/javascript", "application/ecmascript",
        "text/html", "application/xhtml+xml", "image/svg+xml", "application/xml", "text/xml",
    };

    private static bool TryGetAllowedType(string mediaType, out string kind, out string[] extensions)
    {
        switch (mediaType)
        {
            case "image/png": kind = KindImage; extensions = [".png"]; return true;
            case "image/jpeg": kind = KindImage; extensions = [".jpg", ".jpeg"]; return true;
            case "image/gif": kind = KindImage; extensions = [".gif"]; return true;
            case "image/webp": kind = KindImage; extensions = [".webp"]; return true;
            case "audio/wav": kind = KindVoiceNote; extensions = [".wav"]; return true;
            case "audio/ogg": kind = KindVoiceNote; extensions = [".ogg", ".oga", ".opus"]; return true;
            case "audio/opus": kind = KindVoiceNote; extensions = [".opus", ".ogg"]; return true;
            case "audio/mpeg": kind = KindVoiceNote; extensions = [".mp3"]; return true;
            case "audio/webm": kind = KindVoiceNote; extensions = [".webm"]; return true;
            case "audio/mp4": kind = KindVoiceNote; extensions = [".m4a", ".mp4"]; return true;
            case "application/pdf": kind = KindFile; extensions = [".pdf"]; return true;
            case "text/plain": kind = KindFile; extensions = [".txt", ".log"]; return true;
            case "text/markdown": kind = KindFile; extensions = [".md", ".markdown"]; return true;
            case "text/csv": kind = KindFile; extensions = [".csv"]; return true;
            case "application/json": kind = KindFile; extensions = [".json"]; return true;
            default: kind = string.Empty; extensions = []; return false;
        }
    }

    public static bool IsValidAttachmentId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length is < 16 or > 64)
        {
            return false;
        }

        foreach (var ch in id)
        {
            var ok = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '-'
                || ch == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static string SanitizeFileName(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            throw new AttachmentValidationException("Attachment filename is required.");
        }

        var trimmed = rawFileName.Trim();
        if (trimmed.Length > MaxFileNameLength
            || trimmed.IndexOfAny(['/', '\\', '\0', '"', ';']) >= 0
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Any(char.IsControl))
        {
            throw new AttachmentValidationException("Attachment filename contains forbidden path, header, or control characters.");
        }

        return trimmed;
    }

    public static string NormalizeMediaType(string? rawMediaType)
    {
        if (string.IsNullOrWhiteSpace(rawMediaType))
        {
            return string.Empty;
        }

        var semi = rawMediaType.IndexOf(';', StringComparison.Ordinal);
        var core = (semi >= 0 ? rawMediaType[..semi] : rawMediaType).Trim().ToLowerInvariant();
        return core switch
        {
            "image/jpg" => "image/jpeg",
            "audio/x-wav" or "audio/wave" => "audio/wav",
            "audio/mp3" => "audio/mpeg",
            "audio/x-m4a" => "audio/mp4",
            _ => core,
        };
    }

    public static ValidatedAttachment ValidateUpload(
        string? rawFileName,
        string? rawDeclaredMediaType,
        ReadOnlySpan<byte> bytes,
        bool requireVoiceNote = false,
        int? durationMs = null)
    {
        if (bytes.Length == 0)
        {
            throw new AttachmentValidationException("Attachment payload must not be empty.", statusCode: 400);
        }

        if (bytes.Length > MaxFileAttachmentBytes)
        {
            throw new AttachmentValidationException($"Attachment exceeds the {MaxFileAttachmentBytes / (1024 * 1024)} MiB size limit.", statusCode: 413);
        }

        var fileName = SanitizeFileName(rawFileName);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || BlockedExtensions.Contains(ext))
        {
            throw new AttachmentValidationException($"Attachment file extension '{ext}' is not allowed.", statusCode: 415);
        }

        var mediaType = NormalizeMediaType(rawDeclaredMediaType);
        if (string.IsNullOrEmpty(mediaType) || BlockedMediaTypes.Contains(mediaType))
        {
            throw new AttachmentValidationException($"Attachment media type '{rawDeclaredMediaType ?? "unknown"}' is not allowed.", statusCode: 415);
        }

        if (!TryGetAllowedType(mediaType, out var kind, out var extensions))
        {
            throw new AttachmentValidationException($"Unsupported attachment media type '{mediaType}'.", statusCode: 415);
        }

        var kindMaxBytes = MaxBytesForKind(kind);
        if (bytes.Length > kindMaxBytes)
        {
            throw new AttachmentValidationException($"Attachment exceeds the {kindMaxBytes / (1024 * 1024)} MiB size limit for {kind}.", statusCode: 413);
        }

        if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw new AttachmentValidationException($"File extension '{ext}' does not match declared media type '{mediaType}'.", statusCode: 415);
        }

        if (HasDangerousMagicBytes(bytes))
        {
            throw new AttachmentValidationException("Attachment payload contains disallowed executable or active script signatures.", statusCode: 415);
        }

        if (!MatchesMagicBytes(mediaType, bytes))
        {
            throw new AttachmentValidationException($"Attachment content does not match declared media type '{mediaType}'.", statusCode: 415);
        }

        if (requireVoiceNote && !string.Equals(kind, KindVoiceNote, StringComparison.Ordinal))
        {
            throw new AttachmentValidationException("Voice-note upload requires a supported audio media type.", statusCode: 415);
        }

        if (durationMs is not null)
        {
            if (durationMs < 0 || durationMs > MaxVoiceNoteDurationMs)
            {
                throw new AttachmentValidationException($"Voice-note duration must be between 0 and {MaxVoiceNoteDurationMs} ms.", statusCode: 400);
            }
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ValidatedAttachment(kind, fileName, mediaType, bytes.Length, hash, durationMs);
    }

    public static void ValidateTurnAttachments(AttachmentRef[]? attachments)
    {
        if (attachments is not { Length: > 0 })
        {
            return;
        }

        if (attachments.Length > MaxAttachmentsPerTurn)
        {
            throw new ArgumentException($"At most {MaxAttachmentsPerTurn} attachments may be sent per turn.", nameof(attachments));
        }

        long totalBytes = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attachments)
        {
            if (item is null)
            {
                throw new ArgumentException("Attachment entries must not be null.", nameof(attachments));
            }

            if (!IsValidAttachmentId(item.Id))
            {
                throw new ArgumentException($"Attachment id '{item.Id}' is invalid.", nameof(attachments));
            }

            if (!seenIds.Add(item.Id))
            {
                throw new ArgumentException($"Duplicate attachment id '{item.Id}' in turn.", nameof(attachments));
            }

            _ = SanitizeFileName(item.FileName);

            var normalizedMime = NormalizeMediaType(item.MediaType);
            if (!TryGetAllowedType(normalizedMime, out var expectedKind, out _))
            {
                throw new ArgumentException($"Unsupported attachment media type '{item.MediaType}'.", nameof(attachments));
            }

            if (item.Kind is not (KindImage or KindFile or KindVoiceNote)
                || !string.Equals(item.Kind, expectedKind, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Attachment kind '{item.Kind}' does not match media type '{item.MediaType}'.", nameof(attachments));
            }

            var maxForKind = MaxBytesForKind(expectedKind);
            if (item.SizeBytes <= 0 || item.SizeBytes > maxForKind)
            {
                throw new ArgumentException($"Attachment size must be between 1 byte and {maxForKind} bytes.", nameof(attachments));
            }

            totalBytes += item.SizeBytes;
            if (totalBytes > MaxTotalTurnAttachmentBytes)
            {
                throw new ArgumentException($"Combined attachments exceed the {MaxTotalTurnAttachmentBytes / (1024 * 1024)} MiB per-turn limit.", nameof(attachments));
            }

            if (item.DurationMs is < 0 or > MaxVoiceNoteDurationMs)
            {
                throw new ArgumentException($"Voice-note duration must be between 0 and {MaxVoiceNoteDurationMs} ms.", nameof(attachments));
            }

            if (item.Transcript is { Length: > MaxTranscriptChars })
            {
                throw new ArgumentException($"Voice-note transcript exceeds {MaxTranscriptChars} characters.", nameof(attachments));
            }
        }
    }

    private static bool HasDangerousMagicBytes(ReadOnlySpan<byte> bytes)
    {
        // PE/MZ executable (Windows), ELF binary (Linux), Mach-O (macOS), Shebang scripts
        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
        {
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'#' && bytes[1] == (byte)'!')
        {
            return true;
        }

        // Mach-O 64-bit, 32-bit, and fat universal binaries (big & little endian)
        if (bytes.Length >= 4
            && ((bytes[0] == 0xCF && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE)
                || (bytes[0] == 0xFE && bytes[1] == 0xED && bytes[2] == 0xFA && bytes[3] == 0xCF)
                || (bytes[0] == 0xFE && bytes[1] == 0xED && bytes[2] == 0xFA && bytes[3] == 0xCE)
                || (bytes[0] == 0xCE && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE)
                || (bytes[0] == 0xCA && bytes[1] == 0xFE && bytes[2] == 0xBA && bytes[3] == 0xBE)
                || (bytes[0] == 0xBE && bytes[1] == 0xBA && bytes[2] == 0xFE && bytes[3] == 0xCA)))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesMagicBytes(string mediaType, ReadOnlySpan<byte> bytes) => mediaType switch
    {
        "image/png" => bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,

        "image/jpeg" => bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,

        "image/gif" => bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a',

        "image/webp" => bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P',

        "audio/wav" => bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E',

        "audio/ogg" or "audio/opus" => bytes.Length >= 4
            && bytes[0] == (byte)'O' && bytes[1] == (byte)'g' && bytes[2] == (byte)'g' && bytes[3] == (byte)'S',

        "audio/mpeg" => (bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
            || (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0),

        "audio/webm" => bytes.Length >= 4
            && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3,

        "audio/mp4" => bytes.Length >= 8
            && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p',

        "application/pdf" => bytes.Length >= 5
            && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F' && bytes[4] == (byte)'-',

        "text/plain" or "text/markdown" or "text/csv" => IsSafeUtf8Text(bytes, requireJson: false),
        "application/json" => IsSafeUtf8Text(bytes, requireJson: true),
        _ => false,
    };

    private static bool IsSafeUtf8Text(ReadOnlySpan<byte> bytes, bool requireJson)
    {
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var trimmedStart = text.AsSpan().TrimStart('\uFEFF').TrimStart();
        if (trimmedStart.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("<script", StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requireJson && (trimmedStart.Length == 0 || (trimmedStart[0] != '{' && trimmedStart[0] != '[')))
        {
            return false;
        }

        return true;
    }
}
