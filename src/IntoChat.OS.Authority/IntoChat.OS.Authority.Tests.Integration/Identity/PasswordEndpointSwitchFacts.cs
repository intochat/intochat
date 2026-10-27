using System.Net;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

[Trait("host", "own")]
public sealed class PasswordEndpointSwitchFacts
{
    [Fact(Timeout = 120_000)]
    public async Task ByDefaultLoginAndRegisterAreReachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, "Secured", null, ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };

        using var register = await client.PostAsJsonAsync("/identity/register", new { principalId = "ada", password = "correct-password" }, ct);
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "ada", password = "correct-password" }, ct);

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task WhenOffTheRoutesAreUnmapped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        // Open lets anonymous callers through the gate, so only the missing route can answer.
        await using var app = await StartAsync(brain, "Open", false, ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };

        using var register = await client.PostAsJsonAsync("/identity/register", new { principalId = "ada", password = "correct-password" }, ct);
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "ada", password = "correct-password" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task WhenOffLoginAndRegisterAreNoLongerAnonymousButSessionAndLogoutAre()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, "Secured", false, ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };

        using var register = await client.PostAsJsonAsync("/identity/register", new { principalId = "ada", password = "correct-password" }, ct);
        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "ada", password = "correct-password" }, ct);
        using var session = await client.GetAsync("/identity/session", ct);
        using var logout = await client.PostAsync("/identity/logout", null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, session.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(ITestBrain brain, string posture, bool? builtIn, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = posture;
        if (builtIn is { } value) { builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.BuiltInPasswordEndpoints))] = value.ToString(); }
        builder.Services.AddSingleton<IIntoChatOS>(brain);
        builder.Services.AddSingleton(brain.Platform);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.BrainBirths>());
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
