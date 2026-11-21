namespace IntoChat.OS.Host.Surface;

public static class UntrustedText
{
    private const string OpenTag = "<untrusted-web-content";
    private const string CloseTag = "</untrusted-web-content";

    public static string Fence(Uri source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        var host = Clean(source.IsAbsoluteUri && !string.IsNullOrWhiteSpace(source.Host) ? source.Host : source.OriginalString)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return $"<untrusted-web-content source=\"{host}\">\n{Clean(text ?? string.Empty)}\n</untrusted-web-content>";
    }

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = text.Replace(OpenTag, "&lt;untrusted-web-content", StringComparison.OrdinalIgnoreCase);
        return cleaned.Replace(CloseTag, "&lt;/untrusted-web-content", StringComparison.OrdinalIgnoreCase);
    }
}
