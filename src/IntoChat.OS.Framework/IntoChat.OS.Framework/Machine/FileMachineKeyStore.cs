using System.Security.Cryptography;
using IntoChat.OS.Framework.Parts;

namespace IntoChat.OS.Framework.Machine;

// Linux and container default (and the Windows fallback without a TPM): the PKCS#8 private key sealed by the master
// key custody under its own name (not the secrets wrap key) and written to machine-key.sealed, mode 0600, next to
// the machine-id file.
public sealed class FileMachineKeyStore(string directory, IMasterKeyCustody custody) : IMachineKeyStore
{
    public const string FileName = "machine-key.sealed";
    internal const string KeyName = "machine-key";

    private readonly string _file = Path.Combine(directory, FileName);

    public string Custody => "file";

    public ValueTask EnsureKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_file)) { return ValueTask.CompletedTask; }
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var sealedKey = custody.Wrap(KeyName, KeyName, pkcs8);
            // Another store may win the race; its key is kept.
            try { MachineIdFile.WriteOwnerOnly(_file, sealedKey); }
            catch (IOException) when (File.Exists(_file)) { }
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> PublicKeySpkiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Load();
        return ValueTask.FromResult(key.ExportSubjectPublicKeyInfo());
    }

    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Load();
        return ValueTask.FromResult(key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_file);
        return ValueTask.CompletedTask;
    }

    private ECDsa Load()
    {
        byte[] sealedKey;
        try { sealedKey = File.ReadAllBytes(_file); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { throw new MachineKeyUnavailableException(exception); }
        byte[] pkcs8;
        try { pkcs8 = custody.Unwrap(KeyName, KeyName, sealedKey); }
        catch (CryptographicException exception) { throw new MachineKeyUnavailableException(exception); }
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }
}
