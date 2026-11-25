using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Tests.Support.Fakes;

/// <summary>
/// Seals under one random AES-GCM key per brain, kept in memory: what a host without the brain store's keys (TestOS) uses
/// where the product uses the identity module's sealer. The sealed value is real ciphertext, so a fact that reads stored
/// state finds no plaintext in it.
/// </summary>
public sealed class InMemoryBrainSecretSealer : IBrainSecretSealer
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public Task<SealedSecret> Seal(string brainId, string name, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[clear.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(Key(brainId), TagBytes);
        aes.Encrypt(nonce, clear, cipher, tag);
        return Task.FromResult(new SealedSecret { KeyId = BrainSeal.KeyId(brainId), Cipher = Convert.ToBase64String([.. nonce, .. tag, .. cipher]) });
    }

    public Task<string> Open(string brainId, SealedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var packed = Convert.FromBase64String(secret.Cipher);
        var clear = new byte[packed.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(Key(brainId), TagBytes);
        aes.Decrypt(packed.AsSpan(0, NonceBytes), packed.AsSpan(NonceBytes + TagBytes), packed.AsSpan(NonceBytes, TagBytes), clear);
        return Task.FromResult(Encoding.UTF8.GetString(clear));
    }

    private byte[] Key(string brainId) => _keys.GetOrAdd(brainId, static _ => RandomNumberGenerator.GetBytes(32));
}
