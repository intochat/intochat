using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using System.Net;
using System.Security.Cryptography;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Authority.Identity.External;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Framework.Modules.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// An install that trusts an issuer's public key signs a person in from that issuer's ES256 ticket, once per ticket,
// into one principal and one personal brain per issuer and address.
[Trait("host", "own")]
public sealed class ExternalSignInFacts : IDisposable
{
    private const string Issuer = "cloud-test";
    private const string Audience = "https://os.test.example";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task ValidTicketSignsInAndProvisionsAPersonalBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = Client(app);

        using var redeemed = await client.GetAsync(Redeem(Ticket("ada@example.com", "Ada"), "/after"), ct);

        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        Assert.Equal("/after", redeemed.Headers.Location?.OriginalString);
        Assert.True(redeemed.Headers.CacheControl?.NoStore);
        var session = await client.GetFromJsonAsync<IdentityEndpoints.SessionView>("/identity/session", ct);
        var principal = ExternalSignIn.PrincipalOf(Issuer, "ada@example.com");
        Assert.Equal(principal, session!.PrincipalId);
        Assert.Equal(MemberRole.Owner.ToString(), session.Role);
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        Assert.True(await identity.IsRegistered(principal));
        var brains = await identity.BrainsOf(principal);
        Assert.Equal(session.BrainId, Assert.Single(brains).Id);
        Assert.Equal(BrainKind.Personal, brains[0].Kind);
        Assert.True(await identity.BrainExists(BrainScope.Create(session.AccountId, session.BrainId).Id));
    }

    [Fact(Timeout = 120_000)]
    public async Task ReplayedTicketIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        var ticket = Ticket("bea@example.com", "Bea");
        using var first = Client(app);
        using var second = Client(app);

        using var accepted = await first.GetAsync(Redeem(ticket), ct);
        using var replayed = await second.GetAsync(Redeem(ticket), ct);

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Contains(ExternalSignIn.InvalidCode, await replayed.Content.ReadAsStringAsync(ct));
        using var anonymous = await second.GetAsync("/identity/session", ct);
        Assert.Equal(HttpStatusCode.NoContent, anonymous.StatusCode);
    }

    public static TheoryData<string> Refusals => ["expired", "audience", "signature", "issuer", "unverified", "lifetime", "garbage"];

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Refusals))]
    public async Task UntrustworthyTicketIsRefused(string defect)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = Client(app);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = TimeProvider.System.GetUtcNow();
        var ticket = defect switch
        {
            "expired" => Sign(Claims("cal@example.com") with { ExpiresAt = now.AddSeconds(-90) }, _key, new FakeTimeProvider(now.AddSeconds(-120))),
            "audience" => Sign(Claims("cal@example.com") with { Audience = "https://other.example" }, _key),
            "signature" => Sign(Claims("cal@example.com"), otherKey),
            "issuer" => Sign(Claims("cal@example.com") with { Issuer = "someone-else" }, _key),
            "unverified" => Forge(Claims("cal@example.com") with { EmailVerified = false }),
            "lifetime" => Forge(Claims("cal@example.com") with { ExpiresAt = now.AddMinutes(10) }),
            _ => "not.a.ticket",
        };

        using var refused = await client.GetAsync(Redeem(ticket), ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var anonymous = await client.GetAsync("/identity/session", ct);
        Assert.Equal(HttpStatusCode.NoContent, anonymous.StatusCode);
        Assert.False(await brain.Services.GetRequiredService<IIdentityClient>().IsRegistered(ExternalSignIn.PrincipalOf(Issuer, "cal@example.com")));
    }

    [Fact(Timeout = 120_000)]
    public async Task ExistingPrincipalReusesItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var first = Client(app);
        using var second = Client(app);

        (await first.GetAsync(Redeem(Ticket("Dee@Example.com", "Dee")), ct)).Dispose();
        (await second.GetAsync(Redeem(Ticket("dee@example.com", "Dee again")), ct)).Dispose();

        var one = await first.GetFromJsonAsync<IdentityEndpoints.SessionView>("/identity/session", ct);
        var two = await second.GetFromJsonAsync<IdentityEndpoints.SessionView>("/identity/session", ct);
        Assert.Equal((one!.PrincipalId, one.AccountId, one.BrainId), (two!.PrincipalId, two.AccountId, two.BrainId));
        Assert.Single(await brain.Services.GetRequiredService<IIdentityClient>().BrainsOf(one.PrincipalId));
    }

    [Fact(Timeout = 120_000)]
    public async Task ForeignReturnUrlIsRefusedBeforeTheTicketIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = Client(app);
        var ticket = Ticket("eve@example.com", "Eve");

        using var refused = await client.GetAsync(Redeem(ticket, "https://evil.example/"), ct);
        using var accepted = await client.GetAsync(Redeem(ticket), ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task WithoutTrustedIssuersTheRouteIsUnmappedAndPasswordsStayTheDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct, trust: false, posture: "Open");
        using var client = Client(app);

        using var external = await client.GetAsync(Redeem(Ticket("fay@example.com", "Fay")), ct);
        using var register = await client.PostAsJsonAsync("/identity/register", new { principalId = "fay", password = "correct-password" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, external.StatusCode);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task PasswordRegistrationCannotTakeAnExternalPrincipalName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = Client(app);

        using var register = await client.PostAsJsonAsync("/identity/register",
            new { principalId = ExternalSignIn.PrincipalOf(Issuer, "gus@example.com"), password = "correct-password" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);
    }

    [Theory]
    [InlineData("bad name", true, Audience)]
    [InlineData(Issuer, false, Audience)]
    [InlineData(Issuer, true, "")]
    public void MalformedIssuerFailsOnStart(string name, bool validKey, string audience)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Name")] = name;
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:PublicKey")] = validKey ? Spki(_key) : Convert.ToBase64String(new byte[] { 1, 2, 3 });
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Audience")] = audience;
        builder.Services.AddIdentity();
        using var app = builder.Build();

        var error = Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<AuthOptions>>().Value);
        Assert.Contains("TrustedIssuers:0", error.Message);
    }

    [Fact]
    public void RsaKeyIsNotATrustedIssuerKey()
    {
        using var rsa = RSA.Create(2048);
        Assert.Contains("PublicKey", TrustedIssuerKeys.Problem([new TrustedIssuer { Name = Issuer, Audience = Audience, PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) }]));
        Assert.Null(TrustedIssuerKeys.Problem([new TrustedIssuer { Name = Issuer, Audience = Audience, PublicKey = Spki(_key) }]));
        Assert.Contains("twice", TrustedIssuerKeys.Problem([
            new TrustedIssuer { Name = Issuer, Audience = Audience, PublicKey = Spki(_key) },
            new TrustedIssuer { Name = Issuer, Audience = Audience, PublicKey = Spki(_key) }]));
    }

    private ExternalSignInClaims Claims(string email, string name = "Person")
        => new(Issuer, Audience, email, name, true, "google", Guid.NewGuid().ToString("N"), TimeProvider.System.GetUtcNow().AddSeconds(50));

    private string Ticket(string email, string name) => Sign(Claims(email, name), _key);

    private static string Sign(ExternalSignInClaims claims, ECDsa key, TimeProvider? clock = null)
        => ExternalSignInTicket.Sign(claims, key, clock ?? TimeProvider.System);

    // Claims Sign itself refuses to issue: signed by hand, so only the verifier stands between them and a session.
    private string Forge(ExternalSignInClaims claims)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = claims.Issuer }));
        var payload = Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = claims.Issuer,
            aud = claims.Audience,
            exp = claims.ExpiresAt.ToUnixTimeSeconds(),
            jti = claims.Jti,
            email = claims.Email,
            name = claims.Name,
            email_verified = claims.EmailVerified,
            amr = claims.Amr,
        }));
        var signature = _key.SignData(System.Text.Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + payload + "." + Encode(signature);
    }

    private static string Redeem(string ticket, string returnUrl = "/")
        => $"{ExternalSignIn.Path}?t={Uri.EscapeDataString(ticket)}&returnUrl={Uri.EscapeDataString(returnUrl)}";

    private static string Spki(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static HttpClient Client(WebApplication app)
        => new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false }) { BaseAddress = new(app.Urls.Single()) };

    private async Task<WebApplication> StartAsync(ITestBrain brain, CancellationToken ct, bool trust = true, string posture = "Secured")
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = posture;
        if (trust)
        {
            builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Name")] = Issuer;
            builder.Configuration[AuthOptions.Key("TrustedIssuers:0:PublicKey")] = Spki(_key);
            builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Audience")] = Audience;
        }
        builder.Services.AddSingleton(brain.Services.GetRequiredService<INeuronProxies>());
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton<IBrainBirths>(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IBrainAccess>());
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return app;
    }
}
