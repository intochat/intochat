using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework.Machine;
using Xunit;

namespace IntoChat.OS.Testing.Conformance.Machine;

// The custody kit (D-11): every IMachineKeyStore passes the same facts. A subclass returns stores over one location,
// so two calls to CreateStore share the key; SkipReason returns non-null where the custody cannot run here.
public abstract class MachineKeyStoreConformance
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("header.payload");

    protected abstract IMachineKeyStore CreateStore();

    protected virtual string? SkipReason => null;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePublicKeyIsAnUncompressedP256Point()
    {
        var store = Ready();
        await store.EnsureKeyAsync(Token);

        using var key = Import(await store.PublicKeySpkiAsync(Token));
        var point = key.ExportParameters(includePrivateParameters: false);

        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, point.Curve.Oid.Value);
        Assert.Equal(65, 1 + point.Q.X!.Length + point.Q.Y!.Length);
    }

    [Fact]
    public async Task ASignatureVerifiesWithTheExportedPublicKey()
    {
        var store = Ready();
        await store.EnsureKeyAsync(Token);

        var signature = await store.SignAsync(Payload, Token);
        using var key = Import(await store.PublicKeySpkiAsync(Token));

        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(Payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task SigningBeforeAKeyExistsIsUnavailable()
    {
        var store = Ready();

        await Assert.ThrowsAsync<MachineKeyUnavailableException>(async () => await store.SignAsync(Payload, Token));
        await Assert.ThrowsAsync<MachineKeyUnavailableException>(async () => await store.PublicKeySpkiAsync(Token));
    }

    [Fact]
    public async Task ADeletedKeyIsUnavailable()
    {
        var store = Ready();
        await store.EnsureKeyAsync(Token);

        await store.DeleteAsync(Token);

        await Assert.ThrowsAsync<MachineKeyUnavailableException>(async () => await store.PublicKeySpkiAsync(Token));
    }

    [Fact]
    public async Task TwoStoresOverOneLocationShareTheKey()
    {
        var first = Ready();
        await first.EnsureKeyAsync(Token);
        var second = CreateStore();
        await second.EnsureKeyAsync(Token);

        Assert.Equal(await first.PublicKeySpkiAsync(Token), await second.PublicKeySpkiAsync(Token));
    }

    [Fact]
    public async Task EnsuringAnExistingKeyKeepsIt()
    {
        var store = Ready();
        await store.EnsureKeyAsync(Token);
        var before = await store.PublicKeySpkiAsync(Token);

        await store.EnsureKeyAsync(Token);

        Assert.Equal(before, await store.PublicKeySpkiAsync(Token));
        Assert.False(string.IsNullOrWhiteSpace(store.Custody));
    }

    private IMachineKeyStore Ready()
    {
        if (SkipReason is { } reason) { Assert.Skip(reason); }
        return CreateStore();
    }

    private static ECDsa Import(byte[] spki)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key;
    }
}
