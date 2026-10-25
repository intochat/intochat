using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework.Machine;
using IntoChat.OS.Framework.Parts;
using IntoChat.OS.Testing.Conformance.Machine;

namespace IntoChat.OS.Framework.Tests.Unit.Machine;

public sealed class FileMachineKeyStoreFacts : MachineKeyStoreConformance, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intochat-machine-key-" + Guid.NewGuid().ToString("N"));
    private readonly SealingCustody _custody = new(RandomNumberGenerator.GetBytes(32));

    protected override IMachineKeyStore CreateStore() => new FileMachineKeyStore(_root, _custody);

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task TheKeyFileIsSealedAndOwnerOnly()
    {
        var store = CreateStore();
        await store.EnsureKeyAsync(TestContext.Current.CancellationToken);
        var file = Path.Combine(_root, FileMachineKeyStore.FileName);
        var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        Assert.Equal("file", store.Custody);
        // A plain PKCS#8 key would import; the sealed bytes do not.
        using var probe = ECDsa.Create();
        Assert.ThrowsAny<CryptographicException>(() => probe.ImportPkcs8PrivateKey(bytes, out _));
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix file mode is not observable on Windows; the 0600 assertion runs on Linux CI."); }
        else { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file)); }
    }

    [Fact]
    public async Task AKeySealedUnderAnotherMasterKeyIsUnavailable()
    {
        await CreateStore().EnsureKeyAsync(TestContext.Current.CancellationToken);
        var other = new FileMachineKeyStore(_root, new SealingCustody(RandomNumberGenerator.GetBytes(32)));

        await Assert.ThrowsAsync<MachineKeyUnavailableException>(async () => await other.SignAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
    }

    // AES-GCM under one key, the context as associated data: stands in for the configured or TPM custody.
    private sealed class SealingCustody(byte[] key) : IMasterKeyCustody
    {
        public byte[] Wrap(string accountId, string context, byte[] secret)
        {
            var output = new byte[12 + 16 + secret.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), secret, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes(accountId + "|" + context));
            return output;
        }

        public byte[] Unwrap(string accountId, string context, byte[] wrapped)
        {
            var secret = new byte[wrapped.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), secret, Encoding.UTF8.GetBytes(accountId + "|" + context));
            return secret;
        }
    }
}
