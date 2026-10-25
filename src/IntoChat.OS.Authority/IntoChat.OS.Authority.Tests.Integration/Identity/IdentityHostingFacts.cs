using System.Net;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

[Trait("host", "own")]
public sealed class IdentityHostingFacts
{
    [Fact]
    public async Task SharedBrainSessionRequiresMembershipAndUsesTheOwningAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = brain.Platform.Directory();
        var alice = await directory.RegisterAsync("alice", "correct-password", "Alice", ct);
        await directory.RegisterAsync("bob", "correct-password", "Bob", ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Framework.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Framework.IBrainAccess>());
        builder.Services.AddIdentity();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient(new HttpClientHandler()) { BaseAddress = new(app.Urls.Single()) };
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "bob", password = "correct-password" }, ct);
        login.EnsureSuccessStatusCode();
        var target = new { alice.AccountId, alice.BrainId };
        using var denied = await client.PostAsJsonAsync("/identity/session/brain", target, ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await directory.ShareBrainAsync(alice.AccountId, alice.BrainId, "bob", "Bob", IntoChat.OS.Framework.Platform.Identity.MemberRole.Member, ct);
        using var selected = await client.PostAsJsonAsync("/identity/session/brain", target, ct);
        selected.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await client.GetStringAsync("/identity/session", ct));
        Assert.Equal(alice.AccountId, session.RootElement.GetProperty("accountId").GetString());
        using var grants = await client.GetAsync($"/brains/{alice.BrainId}/grants", ct);
        Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
        using var revoke = await client.PostAsJsonAsync($"/brains/{alice.BrainId}/grants/revoke",
            new { AppId = "app", SemanticTypeId = "person.email", Mode = 0 }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        await app.StopAsync(ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeploymentCookieOverridesHostDefaultsRegardlessOfWhenIdentityIsRegistered(bool registerFirst)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))] = "host.session" });
        if (registerFirst) { builder.Services.AddIdentity(); }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))] = "deployment.session" });
        if (!registerFirst) { builder.Services.AddIdentity(); }
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("deployment.session", provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CookieKeysPersistInTheGrainStateStoreWheneverTheBuiltHostHasOne(bool hasStore, bool storeRegisteredAfter)
    {
        var services = new ServiceCollection();
        void AddStore() => services.AddKeyedSingleton(IntoChat.OS.Framework.IntoChatOSNames.GrainState, (_, _) =>
            new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"));
        if (hasStore && !storeRegisteredAfter) { AddStore(); }
        services.AddCookieProtection();
        if (hasStore && storeRegisteredAfter) { AddStore(); }
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IOptions<global::Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>().Value.XmlRepository;
        if (hasStore) { Assert.IsType<CookieProtectionStorage.BlobKeyRepository>(repository); }
        else { Assert.Null(repository); }
    }

    [Fact]
    public void TheHostProtectionIdentityOverridesTheWebApplicationsDefault()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCookieProtection();
        builder.Services.AddIdentity();
        builder.Services.Configure<IdentityHostOptions>(options => options.ProtectionApplicationName = "another-host.v1");
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("another-host.v1", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]
    public async Task CookieChallengesAndForbidsReturnStatusesWithoutRedirects()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Open";
        builder.Services.AddIdentity();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        app.MapGet("/challenge", async context => await context.ChallengeAsync());
        app.MapGet("/forbid", async context => await context.ForbidAsync());
        await app.StartAsync(ct);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(app.Urls.Single()) };
        using var challenge = await client.GetAsync("/challenge", ct);
        using var forbid = await client.GetAsync("/forbid", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbid.StatusCode);
        Assert.Null(challenge.Headers.Location);
        Assert.Null(forbid.Headers.Location);
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task LoginUsesTheHostCookieAndTheAccountGateKeepsCorsOnUnauthorizedResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))] = "bootstrap";
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))] = "bootstrap-password";
        builder.Configuration[KernelCors.AllowedOriginConfigurationKey] = "http://localhost:27880";
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Framework.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.BrainBirths>());
        builder.Services.AddIdentity();
        builder.Services.Configure<IdentityHostOptions>(options => options.CookieName = "another-host.session");
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:27880");
        using var denied = await client.GetAsync("/auth/check", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("http://localhost:27880", Assert.Single(denied.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(denied.Headers.GetValues("Access-Control-Allow-Credentials")));
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bootstrap:bootstrap-password")));
        using var basic = await client.GetAsync("/auth/check", ct);
        Assert.Equal(HttpStatusCode.NoContent, basic.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var principal = "login-" + Guid.NewGuid().ToString("N");
        using var registered = await client.PostAsJsonAsync("/identity/register", new { principalId = principal, password = "correct-password", displayName = "Reader" }, ct);
        registered.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = principal, password = "correct-password" }, ct);
        login.EnsureSuccessStatusCode();
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("another-host.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        using var authenticated = await client.GetAsync("/auth/check", ct);
        Assert.Equal(HttpStatusCode.NoContent, authenticated.StatusCode);
        using var session = await client.GetAsync("/identity/session", ct);
        using var payload = JsonDocument.Parse(await session.Content.ReadAsStringAsync(ct));
        Assert.Contains(principal, payload.RootElement.ToString(), StringComparison.Ordinal);
        await app.StopAsync(ct);
    }
}
