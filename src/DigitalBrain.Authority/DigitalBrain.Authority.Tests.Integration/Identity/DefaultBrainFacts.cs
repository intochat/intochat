using System.Net;
using System.Text.Json;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

[Trait("host", "own")]
public sealed class DefaultBrainFacts
{
    [Fact]
    public async Task OpenPostureActsInTheOwnersDefaultBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, brain) = await Start("Open", ct);
        await using var appHandle = app;
        await using var brainHandle = brain;
        using var client = Client(app);
        using var response = await client.GetAsync("/identity/session", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(AccountSession.DefaultLogin, body.RootElement.GetProperty("principalId").GetString());
        Assert.Equal(AccountSession.DefaultLogin, body.RootElement.GetProperty("accountId").GetString());
        Assert.Equal(AccountSession.DefaultLogin, body.RootElement.GetProperty("brainId").GetString());
    }

    // /mcp stamps the caller AccountSession resolves; the session answers that same caller's brain.
    [Fact]
    public async Task TheShellAndMcpResolveTheSameDefaultBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, brain) = await Start("Open", ct);
        await using var appHandle = app;
        await using var brainHandle = brain;
        using var client = Client(app);
        using var body = JsonDocument.Parse(await client.GetStringAsync("/identity/session", ct));
        var http = new global::Microsoft.AspNetCore.Http.DefaultHttpContext();
        var stamped = AccountSession.ResolveCaller(http, app.Services.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value);
        Assert.NotNull(stamped);
        Assert.Equal(stamped.BrainId, body.RootElement.GetProperty("brainId").GetString());
    }

    [Fact]
    public async Task SecuredPostureWithoutACallerStillAnswersNoContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, brain) = await Start("Secured", ct);
        await using var appHandle = app;
        await using var brainHandle = brain;
        using var client = Client(app);
        using var response = await client.GetAsync("/identity/session", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("owner", "owner", "owner")]
    [InlineData("operator", null, "owner")]
    [InlineData("alice", "alice-home", "alice-home")]
    public void TheDefaultBrainResolvesFromTheClaimAndElseTheOwnerBrain(string accountId, string? claimBrain, string expected)
        => Assert.Equal(expected, AccountSession.DefaultBrain(accountId, claimBrain));

    private static async Task<(WebApplication App, ITestBrain Brain)> Start(string posture, CancellationToken ct)
    {
        var brain = await TestBrain.Create().StartAsync(ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = posture;
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton<Orleans.IGrainFactory>(brain.Grains);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<DigitalBrain.Authority.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<DigitalBrain.Authority.Identity.BrainBirths>());
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return (app, brain);
    }

    private static HttpClient Client(WebApplication app)
        => new() { BaseAddress = new Uri(app.Urls.Single()) };
}
