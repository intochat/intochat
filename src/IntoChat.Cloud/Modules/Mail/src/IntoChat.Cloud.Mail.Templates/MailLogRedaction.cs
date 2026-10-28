using System.Text.RegularExpressions;

namespace IntoChat.Cloud.Mail.Templates;

public static partial class MailLogRedaction
{
    public const string CodeMask = "******";
    public const string TokenMask = "<redacted>";

    public static string RedactUrl(string url)
    {
        var cut = url.Length;
        foreach (var marker in new[] { "#", "?" })
        {
            var index = url.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && index < cut)
            {
                cut = index;
            }
        }

        var invite = url.IndexOf("/invite/", StringComparison.OrdinalIgnoreCase);
        if (invite >= 0 && invite + "/invite/".Length < cut)
        {
            cut = invite + "/invite/".Length;
        }

        return cut == url.Length ? url : url[..cut] + TokenMask;
    }

    public static string RedactBody(string plainText, IReadOnlyDictionary<string, string> data)
    {
        var body = UrlPattern().Replace(plainText, match => RedactUrl(match.Value));
        if (data.TryGetValue(MailDataKeys.Code, out var code) && !string.IsNullOrEmpty(code))
        {
            body = body.Replace(code, CodeMask, StringComparison.Ordinal);
        }

        return body;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
