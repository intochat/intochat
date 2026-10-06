using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class SeededAccountEndpointFacts
{
    [Fact]
    public async Task AnOpenHostListsSeededAccountsAndSignsIntoThemWithoutAPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, "Open", ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };

        using var list = JsonDocument.Parse(await client.GetStringAsync("/identity/seeded-accounts", ct));
        var entry = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("intochat", entry.GetProperty("username").GetString());
        Assert.Equal("IntoChat", entry.GetProperty("displayName").GetString());

        using var login = await client.PostAsync("/identity/seeded-accounts/intochat/login", null, ct);
        login.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await client.GetStringAsync("/identity/session", ct));
        Assert.Equal("intochat", session.RootElement.GetProperty("principalId").GetString());

        using var hidden = await client.PostAsync("/identity/seeded-accounts/os/login", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task ASecuredHostDoesNotRevealOrSignIntoSeededAccounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, "Secured", ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        // Secured hosts need a session first; a registered stranger still gets nothing.
        await brain.Get<DigitalBrain.Framework.Platform.Identity.IIdentityDirectory>(DigitalBrain.Framework.Platform.Identity.IdentityGrains.Directory)
            .RegisterAsync("stranger", "correct-password", "Stranger", ct);
        (await client.PostAsJsonAsync("/identity/login", new { principalId = "stranger", password = "correct-password" }, ct)).EnsureSuccessStatusCode();

        using var list = await client.GetAsync("/identity/seeded-accounts", ct);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        using var login = await client.PostAsync("/identity/seeded-accounts/intochat/login", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    // A seeded name belongs to its seed: registering it first would make a stranger the first-party publisher.
    [Fact]
    public async Task NobodyCanRegisterASeededAccountsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:0:Username"] = "intochat";
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.Enforcement.IBrainAccess>());
        builder.Services.AddIdentity();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };

        using var register = await client.PostAsJsonAsync("/identity/register", new { principalId = "intochat", password = "squatter-password", displayName = "Squatter" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);
        Assert.Null(await brain.Grains.GetGrain<DigitalBrain.Platform.Identity.Authority.IPrincipal>("intochat").DefaultMembership());
    }

    private static async Task<WebApplication> StartAsync(ModuleBrain brain, string posture, CancellationToken ct)
    {
        var seeds = new SeededAccountOptions();
        seeds.Accounts.Add(new() { Username = "intochat", Password = "intochat-development", DisplayName = "IntoChat" });
        seeds.Accounts.Add(new() { Username = "os", DisplayName = "DigitalBrain OS" });
        await new SeededAccountsStartup(brain.Grains, Options.Create(seeds), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), NullLogger<SeededAccountsStartup>.Instance).Execute(ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = posture;
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:0:Username"] = "intochat";
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:0:Password"] = "intochat-development";
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:0:DisplayName"] = "IntoChat";
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:1:Username"] = "os";
        builder.Configuration[SeededAccountOptions.SectionName + ":Accounts:1:DisplayName"] = "DigitalBrain OS";
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.Enforcement.IBrainAccess>());
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return app;
    }
}
