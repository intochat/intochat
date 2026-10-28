using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Mail.Templates;

public static class InviteTokens
{
    public const int MaxTokenLength = 64;
    public const string LinkPath = "/invite#";

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsWellFormed(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));

    public static string LinkFor(string publicBaseUrl, string token) =>
        publicBaseUrl.TrimEnd('/') + LinkPath + token;

    public static bool TryReadToken(string url, out string token)
    {
        token = "";
        var index = url.IndexOf(LinkPath, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var candidate = url[(index + LinkPath.Length)..];
        if (!IsWellFormed(candidate))
        {
            return false;
        }

        token = candidate;
        return true;
    }
}
