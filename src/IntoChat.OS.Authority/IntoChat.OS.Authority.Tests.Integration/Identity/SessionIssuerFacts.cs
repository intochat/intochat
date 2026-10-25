using System.Net;
using System.Security.Claims;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class SessionIssuerFacts
{
    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData(ClaimTypes.Role)]
    [InlineData("os.account")]
    [InlineData("os.brain")]
    public async Task AdditionalClaimsNeverOverridePlatformClaims(string reservedType)
    {
        var member = new Member { PrincipalId = "ada", AccountId = "ada", BrainId = "ada", Role = default, DisplayName = "Ada" };
        var http = new DefaultHttpContext();
        var request = new SessionRequest(member, [new Claim(reservedType, "other")]);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new AccountSessionIssuer().IssueAsync(http, request, TestContext.Current.CancellationToken));

        Assert.Contains(reservedType, error.Message);
    }

    [Fact(Timeout = 120_000)]
    public async Task SelectBrain_CookieWithSidClaim_ReissuedCookieKeepsSidAuthTimeAmr()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Platform.Directory().RegisterAsync("dee", "correct-password", "Dee", ct);
        var member = await SignInAsync(client, "dee", "none", ct);

        using var switched = await client.PostAsJsonAsync("/identity/session/brain", new { accountId = member.AccountId, brainId = member.BrainId }, ct);

        switched.EnsureSuccessStatusCode();
        var claims = await ClaimsAsync(client, ct);
        Assert.Equal(["sid-1"], claims["sid"]);
        Assert.Equal(["1700000000"], claims["auth_time"]);
        Assert.Equal(["pwd", "otp"], claims["amr"]);
        Assert.Equal([member.BrainId], claims["os.brain"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task SelectBrain_NoAdditionalClaims_BehavesAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Platform.Directory().RegisterAsync("eve", "correct-password", "Eve", ct);
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "eve", password = "correct-password" }, ct);
        var member = (await login.Content.ReadFromJsonAsync<Member>(ct))!;

        (await client.PostAsJsonAsync("/identity/session/brain", new { accountId = member.AccountId, brainId = member.BrainId }, ct)).EnsureSuccessStatusCode();

        var claims = await ClaimsAsync(client, ct);
        Assert.Equal(new[] { "os.account", "os.brain", ClaimTypes.NameIdentifier, ClaimTypes.Role }.Order(StringComparer.Ordinal), claims.Keys.Order(StringComparer.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task SelectBrain_CarriedClaims_CannotRebindBrainOrRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Platform.Directory().RegisterAsync("fay", "correct-password", "Fay", ct);
        var other = await brain.Platform.Directory().RegisterAsync("hal", "correct-password", "Hal", ct);
        var member = await SignInAsync(client, "fay", other.BrainId, ct);

        // A brain the caller has no membership in cannot be chosen, whatever the session carries.
        using var refused = await client.PostAsJsonAsync("/identity/session/brain", new { accountId = other.AccountId, brainId = other.BrainId }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var switched = await client.PostAsJsonAsync("/identity/session/brain", new { accountId = member.AccountId, brainId = member.BrainId }, ct);
        switched.EnsureSuccessStatusCode();

        var claims = await ClaimsAsync(client, ct);
        Assert.Equal([other.BrainId], claims["brain_hint"]);
        Assert.Equal([member.BrainId], claims["os.brain"]);
        Assert.Equal([member.AccountId], claims["os.account"]);
        Assert.Equal([member.Role.ToString()], claims[ClaimTypes.Role]);
        var session = await client.GetFromJsonAsync<IdentityEndpoints.SessionView>("/identity/session", ct);
        Assert.Equal((member.PrincipalId, member.AccountId, member.BrainId), (session!.PrincipalId, session.AccountId, session.BrainId));
    }

    // The probe plays a privileged module: it signs a member in through the host issuer with claims of its own.
    private static async Task<Member> SignInAsync(HttpClient client, string principal, string brainHint, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/probe/sign-in", new SignInProbe(principal, "correct-password", brainHint), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Member>(ct))!;
    }

    private static async Task<Dictionary<string, string[]>> ClaimsAsync(HttpClient client, CancellationToken ct)
        => (await client.GetFromJsonAsync<Dictionary<string, string[]>>("/probe/claims", ct))!;

    private static async Task<WebApplication> StartAsync(ITestBrain brain, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IBrainAccess>());
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        app.MapPost("/probe/sign-in", async (SignInProbe input, HttpContext http, PrincipalPasswords passwords) =>
        {
            var member = (await passwords.Authenticate(input.PrincipalId, input.Password).WaitAsync(http.RequestAborted))!;
            Claim[] extra = [new("sid", "sid-1"), new("auth_time", "1700000000"), new("amr", "pwd"), new("amr", "otp"), new("brain_hint", input.BrainHint)];
            var issued = await http.RequestServices.GetRequiredKeyedService<ISessionIssuer>(PrivilegedModuleGate.HostKey)
                .IssueAsync(http, new SessionRequest(member, extra), http.RequestAborted);
            return Results.Ok(issued.Member);
        }).AllowAnonymous();
        app.MapGet("/probe/claims", (HttpContext http) => http.User.Claims.GroupBy(claim => claim.Type).ToDictionary(group => group.Key, group => group.Select(claim => claim.Value).ToArray()));
        await app.StartAsync(ct);
        return app;
    }

    private sealed record SignInProbe(string PrincipalId, string Password, string BrainHint);
}
