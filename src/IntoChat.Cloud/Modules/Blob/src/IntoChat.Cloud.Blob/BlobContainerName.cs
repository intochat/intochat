using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IntoChat.Cloud.Blob;

/// <summary>One container per brain. The name is <c>b-</c> plus 40 hex characters of SHA-256(brain id).</summary>
public static partial class BlobContainerName
{
    public static string For(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(brainId))).ToLowerInvariant();
        var name = "b-" + hex[..40];
        if (!NamePattern().IsMatch(name)) { throw new InvalidOperationException("The container name breaks Azure naming rules."); }
        return name;
    }

    [GeneratedRegex("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
