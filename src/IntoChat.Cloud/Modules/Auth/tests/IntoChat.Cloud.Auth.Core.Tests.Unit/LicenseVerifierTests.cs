using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.Cloud.Auth.Core;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class LicenseVerifierTests : IDisposable
{
    private const string Kid = "test-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _signer.Dispose();

    private LicenseVerifier Verifier(ECDsa? publicKeyOwner = null, string kid = Kid) =>
        new([(kid, Convert.ToBase64String((publicKeyOwner ?? _signer).ExportSubjectPublicKeyInfo()))], _time);

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string json) => B64(Encoding.UTF8.GetBytes(json));

    private string Sign(string headerJson, string payloadJson, ECDsa? key = null)
    {
        var signingInput = B64(headerJson) + "." + B64(payloadJson);
        var signature = (key ?? _signer).SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + B64(signature);
    }

    private static string Header(string alg = "ES256", string kid = Kid) =>
        JsonSerializer.Serialize(new { alg, kid, typ = "JWT" });

    private static string Payload(
        long? exp = null,
        long? nbf = null,
        string iss = "intochat",
        string[]? products = null)
    {
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = iss,
            ["sub"] = "lic-42",
            ["org"] = "Acme",
            ["products"] = products ?? ["auth"],
            ["exp"] = exp ?? Now.AddDays(30).ToUnixTimeSeconds(),
        };
        if (nbf is not null)
        {
            claims["nbf"] = nbf;
        }

        return JsonSerializer.Serialize(claims);
    }

    private string ValidToken() => Sign(Header(), Payload());

    [Fact]
    public void Verify_ValidToken_ReturnsClaims()
    {
        var result = Verifier().Verify(ValidToken());

        Assert.True(result.Ok);
        Assert.Equal("Acme", result.Org);
        Assert.Equal("lic-42", result.LicenseId);
        Assert.Equal(Now.AddDays(30).ToUnixTimeSeconds(), result.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_NullOrWhitespace_Missing(string? token)
    {
        Assert.Equal("missing", Verifier().Verify(token).Reason);
    }

    [Fact]
    public void Verify_TamperedPayload_Invalid()
    {
        var parts = ValidToken().Split('.');
        var forged = B64(Payload(exp: Now.AddYears(50).ToUnixTimeSeconds()));

        Assert.Equal("invalid", Verifier().Verify($"{parts[0]}.{forged}.{parts[2]}").Reason);
    }

    [Fact]
    public void Verify_WrongKey_Invalid()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal("invalid", Verifier(other).Verify(ValidToken()).Reason);
    }

    [Fact]
    public void Verify_UnknownKid_Invalid()
    {
        Assert.Equal("invalid", Verifier().Verify(Sign(Header(kid: "nope"), Payload())).Reason);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256")]
    [InlineData("ES384")]
    public void Verify_DisallowedAlgorithm_Invalid(string alg)
    {
        Assert.Equal("invalid", Verifier().Verify(Sign(Header(alg), Payload())).Reason);
    }

    [Fact]
    public void Verify_AlgNoneWithEmptySignature_Invalid()
    {
        var token = B64(Header("none")) + "." + B64(Payload()) + ".";

        Assert.Equal("invalid", Verifier().Verify(token).Reason);
    }

    [Fact]
    public void Verify_CritHeader_Invalid()
    {
        var header = "{\"alg\":\"ES256\",\"kid\":\"test-1\",\"crit\":[\"exp\"],\"exp\":1}";

        Assert.Equal("invalid", Verifier().Verify(Sign(header, Payload())).Reason);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("a..c")]
    [InlineData("!!!.@@@.###")]
    public void Verify_MalformedStructure_Invalid(string token)
    {
        Assert.Equal("invalid", Verifier().Verify(token).Reason);
    }

    [Fact]
    public void Verify_ShortSignature_Invalid()
    {
        var parts = ValidToken().Split('.');

        Assert.Equal("invalid", Verifier().Verify($"{parts[0]}.{parts[1]}.{B64(new byte[32])}").Reason);
    }

    [Fact]
    public void Verify_PaddedBase64_Invalid()
    {
        var parts = ValidToken().Split('.');

        Assert.Equal("invalid", Verifier().Verify($"{parts[0]}.{parts[1]}.{parts[2]}==").Reason);
    }

    [Fact]
    public void Verify_ProductsWithoutAuth_Invalid()
    {
        Assert.Equal("invalid", Verifier().Verify(Sign(Header(), Payload(products: ["other"]))).Reason);
    }

    [Fact]
    public void Verify_WrongIssuer_Invalid()
    {
        Assert.Equal("invalid", Verifier().Verify(Sign(Header(), Payload(iss: "someone"))).Reason);
    }

    [Fact]
    public void Verify_MissingExp_Invalid()
    {
        var payload = "{\"iss\":\"intochat\",\"sub\":\"x\",\"org\":\"y\",\"products\":[\"auth\"]}";

        Assert.Equal("invalid", Verifier().Verify(Sign(Header(), payload)).Reason);
    }

    [Fact]
    public void Verify_ExpiredBeyondSkew_Expired()
    {
        var exp = Now.AddMinutes(-5).AddSeconds(-1).ToUnixTimeSeconds();

        Assert.Equal("expired", Verifier().Verify(Sign(Header(), Payload(exp: exp))).Reason);
    }

    [Fact]
    public void Verify_ExpiredWithinSkew_Ok()
    {
        var exp = Now.AddMinutes(-5).AddSeconds(1).ToUnixTimeSeconds();

        Assert.True(Verifier().Verify(Sign(Header(), Payload(exp: exp))).Ok);
    }

    [Fact]
    public void Verify_NotBeforeBeyondSkew_Invalid()
    {
        var nbf = Now.AddMinutes(5).AddSeconds(1).ToUnixTimeSeconds();

        Assert.Equal("invalid", Verifier().Verify(Sign(Header(), Payload(nbf: nbf))).Reason);
    }

    [Fact]
    public void Verify_NotBeforeWithinSkew_Ok()
    {
        var nbf = Now.AddMinutes(4).ToUnixTimeSeconds();

        Assert.True(Verifier().Verify(Sign(Header(), Payload(nbf: nbf))).Ok);
    }

    [Fact]
    public void Verify_Failures_NeverEchoTokenOrSegments()
    {
        var valid = ValidToken();
        var parts = valid.Split('.');
        var failing = new[]
        {
            valid + "x",
            $"{parts[0]}.{B64(Payload(iss: "bad"))}.{parts[2]}",
            Sign(Header("HS256"), Payload()),
            Sign(Header(), Payload(exp: Now.AddDays(-1).ToUnixTimeSeconds())),
            "###.###.###",
        };

        using var verifier = Verifier();
        foreach (var token in failing)
        {
            var result = verifier.Verify(token);
            Assert.Contains(result.Reason, new[] { "missing", "invalid", "expired" });
            var rendered = result.ToString() + result.Reason;
            foreach (var segment in token.Split('.').Where(segment => segment.Length > 3))
            {
                Assert.DoesNotContain(segment, rendered, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EmbeddedKeys_AllImportAsP256PublicKeys()
    {
        foreach (var (kid, spki) in LicenseKeys.All)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);

            Assert.False(string.IsNullOrWhiteSpace(kid));
            Assert.Equal(256, ecdsa.KeySize);
        }

        using var verifier = LicenseVerifier.WithEmbeddedKeys();
        Assert.Equal("invalid", verifier.Verify(ValidToken()).Reason);
    }
}
