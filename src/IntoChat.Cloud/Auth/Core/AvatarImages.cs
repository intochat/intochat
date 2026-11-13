namespace IntoChat.Cloud.Auth.Core;

// Avatar pictures are accepted by their leading bytes, never by a declared type or file name.
public static class AvatarImages
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature)) { return Png; }
        return bytes.StartsWith(JpegSignature) ? Jpeg : null;
    }
}
