using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Modules.Devices.Link;

namespace IntoChat.OS.Modules.Devices.Link.Tests.Unit;

public sealed class MachineProofFacts : IDisposable
{
    private static readonly Uri Url = new("https://cloud.example/circle/v1/journal/b1?from=4");
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "access-token";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => key.Dispose();

    private ValueTask<byte[]> Sign(ReadOnlyMemory<byte> data)
        => ValueTask.FromResult(key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private Task<string> Proof(string method = "POST", string nonce = "n1") => MachineProof.CreateAsync(Sign, method, Url, Token, Now, nonce).AsTask();

    private ProofResult Verify(string proof, string method = "POST", Uri? url = null, string token = Token, TimeSpan? after = null, INonceCache? cache = null)
        => MachineProof.Verify(key.ExportSubjectPublicKeyInfo(), proof, method, url ?? Url, token, Now + (after ?? TimeSpan.FromSeconds(30)), cache ?? new Nonces());

    [Fact]
    public async Task AFreshProofVerifies()
        => Assert.True(Verify(await Proof()).IsValid);

    [Fact]
    public void SignHelperProducesAVerifiableProof()
    {
        var proof = MachineProof.Sign(key, "GET", Url, Token, Now);
        Assert.True(Verify(proof, method: "GET").IsValid);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(-61)]
    public async Task SkewOverSixtySecondsFails(int seconds)
        => Assert.Equal(ProofError.Skew, Verify(await Proof(), after: TimeSpan.FromSeconds(seconds)).Error);

    [Fact]
    public async Task ADifferentMethodFailsBinding()
        => Assert.Equal(ProofError.Binding, Verify(await Proof(), method: "PUT").Error);

    [Fact]
    public async Task ADifferentQueryFailsBinding()
        => Assert.Equal(ProofError.Binding, Verify(await Proof(), url: new Uri("https://cloud.example/circle/v1/journal/b1?from=5")).Error);

    [Fact]
    public async Task ADifferentAccessTokenFailsBinding()
        => Assert.Equal(ProofError.Binding, Verify(await Proof(), token: "stolen").Error);

    [Fact]
    public async Task AnotherKeyFailsSignature()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var proof = await Proof();
        var result = MachineProof.Verify(other.ExportSubjectPublicKeyInfo(), proof, "POST", Url, Token, Now, new Nonces());
        Assert.Equal(ProofError.Signature, result.Error);
    }

    [Fact]
    public async Task ATamperedPayloadFailsSignature()
    {
        var parts = (await Proof()).Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])).Replace("POST", "post", StringComparison.Ordinal);
        var forged = parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + "." + parts[2];
        Assert.Equal(ProofError.Signature, Verify(forged).Error);
    }

    [Fact]
    public async Task AUsedNonceIsAReplay()
    {
        var cache = new Nonces();
        var proof = await Proof();
        Assert.True(Verify(proof, cache: cache).IsValid);
        Assert.Equal(ProofError.Replay, Verify(proof, cache: cache).Error);
    }

    [Theory]
    [InlineData("none", MachineProof.Type)]
    [InlineData("HS256", MachineProof.Type)]
    [InlineData("ES384", MachineProof.Type)]
    [InlineData("ES256", "JWT")]
    public async Task OnlyEs256WithTheProofTypeIsAccepted(string alg, string typ)
    {
        var parts = (await Proof()).Split('.');
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""{"alg":"{{alg}}","typ":"{{typ}}"}"""));
        Assert.Equal(ProofError.Algorithm, Verify(header + "." + parts[1] + "." + parts[2]).Error);
        Assert.Equal(ProofError.Algorithm, Verify(header + "." + parts[1] + ".").Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("!!.??.##")]
    public void GarbageIsMalformed(string proof) => Assert.Equal(ProofError.Malformed, Verify(proof).Error);

    [Fact]
    public async Task TheProofIsCanonical()
    {
        var parts = (await Proof()).Split('.');
        Assert.Equal("""{"alg":"ES256","typ":"intochat-machine-proof"}""", Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])));
        var expected = $$"""{"htm":"POST","htu":"{{Url.AbsoluteUri}}","iat":{{Now.ToUnixTimeSeconds()}},"jti":"n1","ath":"{{MachineProof.AccessTokenHash(Token)}}"}""";
        Assert.Equal(expected, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])));
        Assert.Equal(64, Base64Url.DecodeFromChars(parts[2]).Length);
    }

    [Fact]
    public async Task TheMethodIsCaseInsensitiveOnBothSides()
        => Assert.True(Verify(await Proof(method: "post"), method: "Post").IsValid);

    [Fact]
    public void TheFingerprintIsSixteenBase64UrlCharacters()
    {
        var fingerprint = MachineProof.Fingerprint(key.ExportSubjectPublicKeyInfo());
        Assert.Equal(16, fingerprint.Length);
        Assert.Equal(Base64Url.EncodeToString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16], fingerprint);
    }

    private sealed class Nonces : INonceCache
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public bool TryAdd(string jti, DateTimeOffset expires) => seen.Add(jti);
    }
}
