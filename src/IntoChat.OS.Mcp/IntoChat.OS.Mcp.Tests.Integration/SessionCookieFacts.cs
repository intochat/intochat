using System.Net;
using System.Text.Json;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Mcp.Tests.Integration;

public sealed class SessionCookieFacts(McpHost host)
{
    [Fact]
    public async Task RuntimeIssuedCookieAuthenticatesOnASeparateMcpHost()
    {
        var ct = TestContext.Current.CancellationToken;
        var keys = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pr131-cookie-" + Guid.NewGuid().ToString("N")));
        var brain = host.Brain;
        await using var runtime = await Host(false, keys, brain);
        await using var mcp = await Host(true, keys, brain);
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false });
        using var registration = await client.PostAsJsonAsync(runtime.Urls.Single() + "/identity/register",
            new { principalId = "alice", password = "correct-password", displayName = "Alice" }, ct);
        registration.EnsureSuccessStatusCode();
        var cookie = registration.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("intochat.session=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.PostAsJsonAsync(mcp.Urls.Single() + "/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "neurons_context", arguments = new { } } }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("alice", context);
        var selected = JsonDocument.Parse(Result(context).GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
        var aliceBrain = BrainScope.Create(selected.GetProperty("accountId").GetString()!, selected.GetProperty("brainId").GetString()!).Id;
        using var denied = await client.PostAsJsonAsync(mcp.Urls.Single() + "/brains/foreign/mcp",
            new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "neurons_context", arguments = new { } } }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Membership in the selected route is insufficient for a raw foreign key.
        foreach (var (contract, key, expectedError) in new[]
        {
            (typeof(IApp).FullName!, BrainScope.Create("someone", "else").Id + "/apps/example", true),
            (typeof(IApp).FullName!, IApp.Key(aliceBrain, "example"), false),
        })
        {
            using var invocation = await client.PostAsJsonAsync(mcp.Urls.Single() + "/mcp",
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "tools/call",
                    @params = new
                    {
                        name = "neurons_call",
                        arguments = new { contract, key, method = "Read", arguments = "[]" }
                    }
                }, ct);
            invocation.EnsureSuccessStatusCode();
            var body = await invocation.Content.ReadAsStringAsync(ct);
            using var result = JsonDocument.Parse(body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
            Assert.Equal(expectedError, result.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());
        }
    }

    private static JsonElement Result(string sse)
        => JsonDocument.Parse(sse.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]).RootElement.GetProperty("result");

    private static async Task<WebApplication> Host(bool mcp, DirectoryInfo keys, ITestBrain brain)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = mcp ? "IntoChat.OS.Mcp" : "IntoChat" });
        builder.Services.AddGrpc(); // the platform maps the Neurons channel beside its routes
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))] = "intochat.session";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName))] = "IntoChat.v1";
        builder.Services.AddSingleton<INeuronProxies>(brain.Services.GetRequiredService<INeuronProxies>());
        global::IntoChat.OS.Kernel.Composition.KernelTestHooks.ShareGrains(builder.Services, brain.Services);
        if (mcp) { builder.Services.AddIntoChatOSMcp(); }
        else
        {
            builder.Services.AddIdentity();
            // The runtime's identity endpoints sign in through the machine's passwords and the trust clients.
            builder.Services.AddSingleton(brain.Services.GetRequiredService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>());
            foreach (var name in new[] { "IntoChat.OS.Authority.Identity.PrincipalPasswords", "IntoChat.OS.Authority.Identity.BrainBirths" })
            {
                var type = typeof(IntoChat.OS.Authority.PlatformHosting).Assembly.GetType(name, throwOnError: true)!;
                builder.Services.AddSingleton(type, brain.Services.GetRequiredService(type));
            }
        }
        builder.Services.AddDataProtection().PersistKeysToFileSystem(keys);
        // The runtime's protection identity is currently registered separately from AddIdentity.
        if (!mcp) { builder.Services.AddDataProtection().SetApplicationName("IntoChat.v1"); }
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAccountSession();
        if (mcp) { app.MapIntoChatOSMcp(); }
        else { app.MapIntoChatOSPlatform(); }
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
