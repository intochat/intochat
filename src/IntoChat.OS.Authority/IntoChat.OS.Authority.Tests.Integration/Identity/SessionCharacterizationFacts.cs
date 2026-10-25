using System.Security.Claims;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// Pins what login, register and select-brain put in the session cookie. These facts must stay
// byte-identical across the ISessionIssuer extraction.
[Trait("host", "own")]
public sealed class SessionCharacterizationFacts
{
    [Fact(Timeout = 120_000)]
    public async Task LoginIssuesTheSessionCookieWithTheMemberClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = new CountingHandler();
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, counter, ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        await brain.Platform.Directory().RegisterAsync("ada", "correct-password", "Ada", ct);

        using var response = await client.PostAsJsonAsync("/identity/login", new { principalId = "ada", password = "correct-password" }, ct);

        response.EnsureSuccessStatusCode();
        var member = (await response.Content.ReadFromJsonAsync<Member>(ct))!;
        AssertSession(app, response, member);
        Assert.Equal(1, counter.Count);
    }

    [Fact(Timeout = 120_000)]
    public async Task RegisterIssuesTheSessionCookieWithTheMemberClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = new CountingHandler();
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, counter, ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };

        using var response = await client.PostAsJsonAsync("/identity/register", new { principalId = "bea", password = "correct-password", displayName = "Bea" }, ct);

        response.EnsureSuccessStatusCode();
        var member = (await response.Content.ReadFromJsonAsync<Member>(ct))!;
        Assert.Equal("bea", member.PrincipalId);
        AssertSession(app, response, member);
        Assert.Equal(1, counter.Count);
    }

    [Fact(Timeout = 120_000)]
    public async Task SelectBrainReissuesTheSessionCookieForTheChosenMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = new CountingHandler();
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, counter, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Platform.Directory().RegisterAsync("cy", "correct-password", "Cy", ct);
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "cy", password = "correct-password" }, ct);
        login.EnsureSuccessStatusCode();
        var first = (await login.Content.ReadFromJsonAsync<Member>(ct))!;

        using var response = await client.PostAsJsonAsync("/identity/session/brain", new { accountId = first.AccountId, brainId = first.BrainId }, ct);

        response.EnsureSuccessStatusCode();
        var member = (await response.Content.ReadFromJsonAsync<Member>(ct))!;
        Assert.Equal(first, member);
        AssertSession(app, response, member);
        Assert.Equal(2, counter.Count);
    }

    private static void AssertSession(WebApplication app, HttpResponseMessage response, Member member)
    {
        var host = app.Services.GetRequiredService<IOptions<IdentityHostOptions>>().Value;
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var parts = setCookie.Split(';', StringSplitOptions.TrimEntries);
        var (name, value) = (parts[0][..parts[0].IndexOf('=')], parts[0][(parts[0].IndexOf('=') + 1)..]);
        Assert.Equal("os.session", name);
        Assert.Equal(host.CookieName, name);
        Assert.Contains("path=/", parts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("httponly", parts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", parts, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", parts, StringComparer.OrdinalIgnoreCase);

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(value));
        Assert.NotNull(ticket);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, ticket.AuthenticationScheme);
        var identity = Assert.Single(ticket.Principal.Identities);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, identity.AuthenticationType);
        Assert.Equal(
            [
                (ClaimTypes.NameIdentifier, member.PrincipalId),
                (host.AccountClaim, member.AccountId),
                (host.BrainClaim, member.BrainId),
                (ClaimTypes.Role, member.Role.ToString()),
            ],
            identity.Claims.Select(claim => (claim.Type, claim.Value)).ToArray());
    }

    private static async Task<WebApplication> StartAsync(ITestBrain brain, ISignInHandler handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IBrainAccess>());
        builder.Services.AddSingleton(handler);
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return app;
    }

    private sealed class CountingHandler : ISignInHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task SignedIn(Member member, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
