using System.Net;
using System.Security.Cryptography;
using IntoChat.OS.Authority.Hosted;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Authority.Identity.Configuration;
using IntoChat.OS.Authority.Identity.External;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Sdk.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// A hosted allocation is container management only: an ext- principal from external sign-in is admitted like a member
// on any install, and the hosted package carries no identity or brain-access code of its own.
[Trait("host", "own")]
public sealed class HostedExternalSignInFacts : IDisposable
{
    private const string Issuer = "cloud-test";
    private const string Audience = "https://hosted.test.example";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task ExternalPrincipalSignsInOnAHostedAllocationAndReachesItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false }) { BaseAddress = new(app.Urls.Single()) };
        var ticket = ExternalSignInTicket.Sign(new ExternalSignInClaims(Issuer, Audience, "ada@example.com", "Ada", true, "google",
            Guid.NewGuid().ToString("N"), TimeProvider.System.GetUtcNow().AddSeconds(50)), _key, TimeProvider.System);

        using var redeemed = await client.GetAsync($"{ExternalSignIn.Path}?t={Uri.EscapeDataString(ticket)}&returnUrl=%2F", ct);

        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        var session = await client.GetFromJsonAsync<IdentityEndpoints.SessionView>("/identity/session", ct);
        Assert.Equal(ExternalSignIn.PrincipalOf(Issuer, "ada@example.com"), session!.PrincipalId);
        Assert.NotEqual("hosted-brain", session.BrainId);
        using var observed = await client.GetAsync($"/brains/{session.BrainId}/management/v1/observation", ct);
        Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
        var body = await observed.Content.ReadFromJsonAsync<RuntimeObservation>(ct);
        Assert.True(body!.Registered);
        Assert.True(await app.Services.GetRequiredService<IBrainAccess>().CanAccessAsync(session.PrincipalId, session.AccountId, session.BrainId, ct));
        // Management still answers only the controller's certificate, never a signed-in person.
        using var ensure = await client.PostAsync("/runtime/management/v1/ensure", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, ensure.StatusCode);
        using var observation = await client.GetAsync("/runtime/management/v1/observation", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, observation.StatusCode);
    }

    [Fact]
    public void HostedPackageCarriesNoIdentityCode()
    {
        var types = typeof(HostedRuntimeOptions).Assembly.GetTypes();
        Assert.DoesNotContain(types, type => typeof(IBrainAccess).IsAssignableFrom(type));
        Assert.DoesNotContain(types, type => type.Name.Contains("BrainAccess", StringComparison.Ordinal) ||
            type.Name.Contains("Workspace", StringComparison.Ordinal) || type.Name.Contains("Session", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(HostedRuntimeOptions).GetProperties(), property =>
            property.Name is "StateDirectory" || property.Name.EndsWith("Claim", StringComparison.Ordinal));

        var services = new ServiceCollection().AddHostedAuthority(Scope(new ConfigurationBuilder()).Build());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IBrainAccess));
    }

    private static T Scope<T>(T configuration) where T : IConfigurationBuilder
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostedRuntimeOptions.SectionName + ":AccountId"] = "hosted-account",
            [HostedRuntimeOptions.SectionName + ":BrainId"] = "hosted-brain",
            [HostedRuntimeOptions.SectionName + ":RuntimeId"] = "allocation-one",
            [HostedRuntimeOptions.SectionName + ":ControllerCertificateSha256"] = new string('a', 64),
        });
        return configuration;
    }

    private async Task<WebApplication> StartAsync(ITestBrain brain, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Name")] = Issuer;
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:PublicKey")] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        builder.Configuration[AuthOptions.Key("TrustedIssuers:0:Audience")] = Audience;
        Scope(builder.Configuration);
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton<IBrainBirths>(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IBrainAccess>());
        builder.Services.AddIdentity();
        builder.Services.AddHostedAuthority(builder.Configuration);
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        RuntimeManagementEndpoints.Map(app);
        HostedRuntimeEndpoints.Map(app);
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return app;
    }
}
