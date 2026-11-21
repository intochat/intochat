using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans;

namespace IntoChat.OS.Runtime;

// A 12-character Crockford base32 code (60 bits), normalized as "ABCD-EFGH-JKMN". Only an HMAC-SHA256 hash of its
// 12 characters is ever stored.
[GenerateSerializer, Alias("os.code"), JsonConverter(typeof(CodeJsonConverter))]
public readonly record struct Code([property: Id(0)] string Value)
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;
    public const int MaxMisses = 20;
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    public static Code New()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new Code(Format(chars));
    }

    // Accepts any case, dashes and spaces; I and L read as 1, O as 0; rejects dots (old friend format) and invalid chars.
    public static bool TryParse(string? input, out Code code)
    {
        code = default;
        if (input is null || input.Contains('.', StringComparison.Ordinal)) { return false; }

        Span<char> chars = stackalloc char[Length];
        var count = 0;
        foreach (var raw in input.Trim())
        {
            if (raw is '-' or ' ') { continue; }
            var c = char.ToUpperInvariant(raw) switch { 'I' or 'L' => '1', 'O' => '0', var other => other };
            if (!Alphabet.Contains(c, StringComparison.Ordinal) || count == Length) { return false; }
            chars[count++] = c;
        }
        if (count != Length) { return false; }
        code = new Code(Format(chars));
        return true;
    }

    // Base64url HMAC-SHA256 of the 12 normalized characters; throws for a malformed code.
    public string Hash(ReadOnlySpan<byte> key)
    {
        if (!TryParse(Value, out var normalized)) { throw new FormatException("Not a valid code."); }
        var mac = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(normalized.Value.Replace("-", "", StringComparison.Ordinal)));
        return Base64Url(mac);
    }

    public static bool HashesEqual(string? left, string? right)
        => left is not null && right is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    public override string ToString() => Value ?? string.Empty;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Format(ReadOnlySpan<char> chars)
        => new StringBuilder(14).Append(chars[..4]).Append('-').Append(chars[4..8]).Append('-').Append(chars[8..]).ToString();
}

public sealed class CodeJsonConverter : JsonConverter<Code>
{
    public override Code Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, Code value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value ?? string.Empty);

    public override Code ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Code value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value ?? string.Empty);
}
