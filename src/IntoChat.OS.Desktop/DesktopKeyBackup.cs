using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// A passphrase-protected copy of the desktop master key. Uninstalling the MSIX deletes LocalState, which holds the only
// (DPAPI-protected) copy; without this backup a reinstall cannot read the brain store. PBKDF2-SHA256 derives an AES-256
// key from the passphrase; AES-GCM seals the master key, and the header (format, version, kdf, work factor, salt, nonce)
// is authenticated as associated data, so a lowered work factor or any edited byte is refused. No new dependency: both
// primitives ship with .NET, which the AOT launcher needs.
internal static class DesktopKeyBackup
{
    public const string Format = "intochat-os-master-key";
    public const int Version = 1;
    public const string Kdf = "pbkdf2-sha256";
    public const int DefaultIterations = 600_000;
    public const int MinimumPassphraseLength = 12;
    private const int MinimumIterations = 1_000;
    private const int MaximumIterations = 10_000_000;

    public static string Export(string masterKey, string passphrase, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterKey);
        if (passphrase is null || passphrase.Length < MinimumPassphraseLength)
        { throw new ArgumentException($"Use a passphrase of at least {MinimumPassphraseLength} characters.", nameof(passphrase)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinimumIterations);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = Encoding.UTF8.GetBytes(masterKey);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var key = Derive(passphrase, salt, iterations);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Header(iterations, salt, nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("version", Version);
            json.WriteString("kdf", Kdf);
            json.WriteNumber("iterations", iterations);
            json.WriteString("salt", Convert.ToBase64String(salt));
            json.WriteString("nonce", Convert.ToBase64String(nonce));
            json.WriteString("ciphertext", Convert.ToBase64String(ciphertext));
            json.WriteString("tag", Convert.ToBase64String(tag));
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Import(string backup, string passphrase)
    {
        byte[] salt, nonce, ciphertext, tag;
        int iterations;
        try
        {
            using var document = JsonDocument.Parse(backup);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("format").GetString() != Format
                || root.GetProperty("version").GetInt32() != Version
                || root.GetProperty("kdf").GetString() != Kdf)
            { throw new CryptographicException("This is not an IntoChat OS master key backup this version can read."); }
            iterations = root.GetProperty("iterations").GetInt32();
            salt = Convert.FromBase64String(root.GetProperty("salt").GetString()!);
            nonce = Convert.FromBase64String(root.GetProperty("nonce").GetString()!);
            ciphertext = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);
            tag = Convert.FromBase64String(root.GetProperty("tag").GetString()!);
        }
        catch (Exception unreadable) when (unreadable is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        { throw new CryptographicException("This is not an IntoChat OS master key backup this version can read.", unreadable); }
        if (iterations is < MinimumIterations or > MaximumIterations || salt.Length != 16
            || nonce.Length != AesGcm.NonceByteSizes.MaxSize || tag.Length != AesGcm.TagByteSizes.MaxSize || ciphertext.Length is 0 or > 4096)
        { throw new CryptographicException("This is not an IntoChat OS master key backup this version can read."); }
        var key = Derive(passphrase ?? "", salt, iterations);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Header(iterations, salt, nonce));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (AuthenticationTagMismatchException mismatch)
        { throw new CryptographicException("The passphrase is wrong or the backup was changed.", mismatch); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // A restore never replaces a key: an existing key file may protect data this backup does not belong to.
    public static void RequireNoKey(string keyPath)
    {
        if (File.Exists(keyPath))
        { throw new InvalidOperationException($"A master key already exists at {keyPath}. Import restores a key only where none exists; move that file away first if you are sure it is not in use."); }
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Header(int iterations, byte[] salt, byte[] nonce)
        => Encoding.UTF8.GetBytes(string.Join('|', Format, Version.ToString(CultureInfo.InvariantCulture), Kdf,
            iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(nonce)));
}
