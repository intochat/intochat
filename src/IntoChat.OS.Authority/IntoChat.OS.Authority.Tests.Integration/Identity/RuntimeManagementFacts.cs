using System.Net;
using System.Text.Json;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

[Trait("host", "own")]
public sealed class RuntimeManagementFacts
{
    [Theory]
    [InlineData("alice", true, HttpStatusCode.OK, "Secured")]
    [InlineData("bob", true, HttpStatusCode.NotFound, "Secured")]
    [InlineData("bob", false, HttpStatusCode.NotFound, "Secured")]
    [InlineData(null, false, HttpStatusCode.Unauthorized, "Secured")]
    [InlineData(null, false, HttpStatusCode.Unauthorized, "Open")]
    public async Task ObservationRequiresThePersistedBrainOwner(string? login, bool share, HttpStatusCode expected, string posture)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var directory = brain.Platform.Directory();
        var alice = await directory.RegisterAsync("alice", "correct-password", "Alice", ct);
        await directory.RegisterAsync("bob", "correct-password", "Bob", ct);
        if (share) { await directory.ShareBrainAsync(alice.AccountId, alice.BrainId, "bob", "Bob", MemberRole.Member, ct); }

        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = posture;
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<PrincipalPasswords>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddIdentity();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        RuntimeManagementEndpoints.Map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient(new HttpClientHandler()) { BaseAddress = new(app.Urls.Single()) };
        if (login is not null)
        {
            using var signedIn = await client.PostAsJsonAsync("/identity/login", new { principalId = login, password = "correct-password" }, ct);
            signedIn.EnsureSuccessStatusCode();
        }

        using var response = await client.GetAsync($"/brains/{alice.BrainId}/management/v1/observation", ct);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(1, payload.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(alice.AccountId, payload.RootElement.GetProperty("accountId").GetString());
            Assert.Equal(alice.BrainId, payload.RootElement.GetProperty("brainId").GetString());
            Assert.True(payload.RootElement.GetProperty("registered").GetBoolean());
            Assert.Equal("observe", Assert.Single(payload.RootElement.GetProperty("capabilities").EnumerateArray()).GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
            const string missingBrain = "unregistered-brain";
            var missingScope = IntoChat.OS.Framework.BrainScope.Create(alice.AccountId, missingBrain).Id;
            Assert.False(await identity.BrainExists(missingScope));
            using var missing = await client.GetAsync($"/brains/{missingBrain}/management/v1/observation", ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.False(await identity.BrainExists(missingScope));
        }
        await app.StopAsync(ct);
    }
}
