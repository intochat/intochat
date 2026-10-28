using System.Buffers.Text;

namespace IntoChat.Cloud.Auth.Core;

public enum CodePurpose : byte
{
    Signup = 1,
    Login = 2,
    Reset = 3,
    ChangeEmail = 4,
}

/// <summary>Opaque code id: base64url of {purposeByte}{subjectHash128}{nonce64}.</summary>
public static class CodeId
{
    public const int SubjectHashLength = 16;
    public const int NonceLength = 8;
    private const int TotalLength = 1 + SubjectHashLength + NonceLength;

    public static string Encode(CodePurpose purpose, ReadOnlySpan<byte> subjectHash128, ReadOnlySpan<byte> nonce64)
    {
        if (subjectHash128.Length != SubjectHashLength)
        {
            throw new ArgumentException("Subject hash must be 128 bits.", nameof(subjectHash128));
        }

        if (nonce64.Length != NonceLength)
        {
            throw new ArgumentException("Nonce must be 64 bits.", nameof(nonce64));
        }

        Span<byte> buffer = stackalloc byte[TotalLength];
        buffer[0] = (byte)purpose;
        subjectHash128.CopyTo(buffer[1..]);
        nonce64.CopyTo(buffer[(1 + SubjectHashLength)..]);
        return Base64Url.EncodeToString(buffer);
    }

    public static bool TryParse(string? value, out CodePurpose purpose, out byte[] subjectHash128, out byte[] nonce64)
    {
        purpose = default;
        subjectHash128 = [];
        nonce64 = [];
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != TotalLength || !Enum.IsDefined((CodePurpose)bytes[0]))
        {
            return false;
        }

        purpose = (CodePurpose)bytes[0];
        subjectHash128 = bytes[1..(1 + SubjectHashLength)];
        nonce64 = bytes[(1 + SubjectHashLength)..];
        return true;
    }
}