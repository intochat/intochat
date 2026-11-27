using System.Reflection;
using System.Security.Cryptography;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.External;

namespace IntoChat.OS.Authority;

// AuthKeys names where AuthOptions binds from, so hosts write the keys without seeing the options (or the password):
// every key is a property of AuthOptions under AuthKeys.SectionName, and every AuthOptions property has its key.
public sealed class AuthKeysFacts
{
    private static readonly Dictionary<string, string> Keys = typeof(AuthKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.Name != nameof(AuthKeys.SectionName))
        .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);

    private static readonly string[] Properties = typeof(AuthOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void EveryKeyIsTheSectionAndThePropertyOfTheSameName()
    {
        Assert.All(Keys, entry => Assert.Equal(AuthKeys.SectionName + ":" + entry.Key, entry.Value));
    }

    [Fact]
    public void KeysAndOptionsNameTheSameSettings()
    {
        Assert.Equal(Properties, Keys.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ServiceSignerRejectsTrailingBytesInBase64Pkcs8PrivateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validPkcs8 = key.ExportPkcs8PrivateKey();
        byte[] withTrailing = [.. validPkcs8, 0x00];

        Assert.Throws<CryptographicException>(() => ServiceAssertionSigner.ImportPrivateKey(Convert.ToBase64String(withTrailing)));

        var problem = ServiceAssertionSigner.Problem(new ServiceSignerOptions
        {
            Issuer = "os.test.intochat.io",
            Audience = "https://cloud.example",
            ServiceUrl = "https://cloud.example",
            PrivateKey = Convert.ToBase64String(withTrailing),
        });
        Assert.NotNull(problem);
        Assert.Contains("PrivateKey", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://cloud.example", true)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://intochat-test-cloud", true)]
    [InlineData("http://cloud.example", false)]
    [InlineData("http://relay.test.intochat.io", false)]
    [InlineData("https://user:pass@cloud.example", false)]
    public void ServiceSignerEnforcesHttpsForExternalHosts(string serviceUrl, bool expectedValid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var problem = ServiceAssertionSigner.Problem(new ServiceSignerOptions
        {
            Issuer = "os.test.intochat.io",
            Audience = "https://cloud.example",
            ServiceUrl = serviceUrl,
            PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
        });

        if (expectedValid)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains("ServiceUrl", problem, StringComparison.Ordinal);
        }
    }
}
