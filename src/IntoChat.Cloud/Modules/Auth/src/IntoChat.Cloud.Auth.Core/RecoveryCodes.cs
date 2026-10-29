using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Auth.Core;

public sealed record RecoveryUseResult(bool Matched, IReadOnlyList<string> Remaining);

public sealed class RecoveryCodes(byte[] rootKey)
{
    public const int Count = 10;
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";
    private const int HalfLength = 5;
    private readonly PurposeHasher _hasher = new("auth-recovery", rootKey);

    public (IReadOnlyList<string> Codes, IReadOnlyList<string> Hashes) Generate()
    {
        var codes = new List<string>(Count);
        var hashes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var raw = new StringBuilder();
            for (var j = 0; j < HalfLength * 2; j++)
            {
                raw.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            }

            var normalised = raw.ToString();
            codes.Add($"{normalised[..HalfLength]}-{normalised[HalfLength..]}");
            hashes.Add(_hasher.Hash(normalised));
        }

        return (codes, hashes);
    }

    public RecoveryUseResult Use(string? code, IReadOnlyList<string> hashes)
    {
        var normalised = Normalise(code);
        if (normalised.Length != HalfLength * 2)
        {
            return new RecoveryUseResult(false, hashes);
        }

        var matchIndex = -1;
        for (var i = 0; i < hashes.Count; i++)
        {
            if (_hasher.Verify(normalised, hashes[i]) && matchIndex < 0)
            {
                matchIndex = i;
            }
        }

        if (matchIndex < 0)
        {
            return new RecoveryUseResult(false, hashes);
        }

        return new RecoveryUseResult(true, hashes.Where((_, i) => i != matchIndex).ToList());
    }

    private static string Normalise(string? code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
}
