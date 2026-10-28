using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Auth.Core;

public enum PasswordVerification
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>PBKDF2-SHA256 in a versioned string: pbkdf2-sha256${iterations}${saltB64}${hashB64}.</summary>
public sealed class PasswordHasher(int iterations = PasswordHasher.DefaultIterations)
{
    public const int DefaultIterations = 600_000;
    public const int MinLength = 12;
    public const int MaxLength = 256;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    public int Iterations { get; } = iterations > 0 ? iterations : throw new ArgumentOutOfRangeException(nameof(iterations));

    public static bool IsValidLength(string? password) =>
        password is not null && password.Length is >= MinLength and <= MaxLength;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public PasswordVerification Verify(string password, string? stored)
    {
        if (password is null || stored is null)
        {
            return PasswordVerification.Failed;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var storedIterations) || storedIterations <= 0)
        {
            return PasswordVerification.Failed;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }

        if (expected.Length != HashBytes || salt.Length == 0)
        {
            return PasswordVerification.Failed;
        }

        var actual = Derive(password, salt, storedIterations);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        return storedIterations < Iterations ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}