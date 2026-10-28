using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Tests;

// A per-run ES256 key standing in for the owner's signing key: tests sign their own tokens and verify them with a
// verifier that trusts only this key. The production key slot is never touched.
internal static class TestLicense
{
    private const string Kid = "test-run";
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    // LicenseVerifier's constructor is internal to Auth.Core (os grants no assembly its internals beyond its own tests), so the test
    // key reaches it through reflection.
    public static LicenseVerifier Verifier(TimeProvider? clock = null) =>
        (LicenseVerifier)Activator.CreateInstance(typeof(LicenseVerifier), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [new[] { (Kid, Convert.ToBase64String(Signer.ExportSubjectPublicKeyInfo())) }, clock ?? TimeProvider.System], null)!;

    public static string Valid(string org = "Test Org") => Token(DateTimeOffset.UtcNow.AddDays(90), org);

    public static string Expired() => Token(DateTimeOffset.UtcNow.AddDays(-2));

    public static string Token(DateTimeOffset expiresAt, string org = "Test Org")
    {
        var header = JsonSerializer.Serialize(new { alg = "ES256", kid = Kid, typ = "JWT" });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "intochat",
            ["sub"] = "lic-test",
            ["org"] = org,
            ["products"] = new[] { "auth" },
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
        });
        var input = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(payload));
        var signature = Signer.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return input + "." + B64(signature);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
