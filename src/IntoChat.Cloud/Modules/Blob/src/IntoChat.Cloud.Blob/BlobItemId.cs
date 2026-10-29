using System.Text;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.Cloud.Blob;

/// <summary>An opaque id: <c>bb:</c> plus the base64url of a blob path. The path is never an absolute filesystem path.</summary>
internal static class BlobItemId
{
    internal const string Prefix = "bb:";

    internal static ItemId Of(string relative)
    {
        var text = (relative ?? "").Replace('\\', '/').Trim('/');
        return new ItemId(Prefix + Base64Url(Encoding.UTF8.GetBytes(text)));
    }

    internal static string Read(ItemId id)
    {
        if (id.Value is not { } value || !value.StartsWith(Prefix, StringComparison.Ordinal)) { throw new NotFound(id); }
        string text;
        try { text = Encoding.UTF8.GetString(Base64UrlDecode(value[Prefix.Length..])); }
        catch (FormatException) { throw new NotFound(id); }
        if (Hostile(text)) { throw new Denied(id); }
        return text;
    }

    internal static bool Hostile(string relative)
    {
        if (relative.Length > 1024) { return true; }
        if (relative.Contains('\\') || relative.Contains("//")) { return true; }
        if (relative.Any(ch => ch < ' ' || ch == '\u007f')) { return true; }
        if (relative.Length == 0) { return false; }
        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or "..") { return true; }
        }
        return false;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
