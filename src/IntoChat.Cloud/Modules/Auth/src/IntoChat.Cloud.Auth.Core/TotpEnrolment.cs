using System.Security.Cryptography;
using OtpNet;

namespace IntoChat.Cloud.Auth.Core;

public sealed record TotpEnrolment(byte[] Secret, string Base32Secret, string OtpAuthUri)
{
    public static TotpEnrolment Create(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var secret = RandomNumberGenerator.GetBytes(20);
        var base32 = Base32Encoding.ToString(secret);
        var uri = $"otpauth://totp/IntoChat:{Uri.EscapeDataString(label)}?secret={base32}&issuer=IntoChat&digits=6&period=30";
        return new TotpEnrolment(secret, base32, uri);
    }
}
