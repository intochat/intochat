using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Mail;

internal sealed class HmacRecipientHasher : IRecipientHasher
{
    private byte[]? key;

    public void UseKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        key = Encoding.UTF8.GetBytes(value);
    }

    public string Hash(string purpose, string email)
    {
        if (key is not { } secret)
        {
            throw new InvalidOperationException("Mail recipient-hashing key is not configured.");
        }

        var input = Encoding.UTF8.GetBytes(purpose + ":" + email.Trim().ToUpperInvariant());
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, input));
    }
}
