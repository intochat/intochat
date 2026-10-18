using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Mcp.Tests.Integration;

public sealed class McpIdentityFacts
{
    // The shell reads the same seam through /identity/session (DefaultBrainFacts in Platform).
    [Theory]
    [InlineData("Open", false)]
    [InlineData("Secured", true)]
    public async Task TheDefaultMcpRouteActsInTheOwnersDefaultBrain(string posture, bool credentials)
    {
        await using var app = await Start(posture, credentials: credentials);
        using var client = Client(app);
        if (credentials)
        {
            client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("operator:test-password")));
        }
        using var response = await Call(client, "/mcp");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var result = JsonDocument.Parse(body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
        using var caller = JsonDocument.Parse(result.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(AccountSession.DefaultLogin, caller.RootElement.GetProperty("brainId").GetString());
    }

    [Fact]
    public async Task OpenMcpStampsTheOwnerAndRequestedBrainForEachToolCall()
    {
        await using var app = await Start("Open");
        using var client = Client(app);
        foreach (var brain in new[] { "first", "second", "first" })
        {
            using var response = await Call(client, $"/brains/{brain}/mcp");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var data = body.Split('\n').Single(line => line.StartsWith("data: "))[6..];
            using var result = JsonDocument.Parse(data);
            using var caller = JsonDocument.Parse(result.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("owner", caller.RootElement.GetProperty("principalId").GetString());
            Assert.Equal(brain, caller.RootElement.GetProperty("brainId").GetString());
        }
    }

    [Fact]
    public async Task SecuredMcpRejectsAnonymousRequests()
    {
        await using var app = await Start("Secured");
        using var client = Client(app);
        using var response = await Call(client, "/brains/first/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SecuredMcpUsesTheExistingBasicIdentityWithoutAcceptingCallerHeaders()
    {
        await using var app = await Start("Secured", credentials: true);
        using var client = Client(app);
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("operator:test-password")));
        client.DefaultRequestHeaders.Add("X-Brain-Id", "forged");
        using var response = await Call(client, "/brains/selected/mcp");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var result = JsonDocument.Parse(body.Split('\n').Single(line => line.StartsWith("data: "))[6..]);
        using var caller = JsonDocument.Parse(result.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("operator", caller.RootElement.GetProperty("principalId").GetString());
        Assert.Equal("operator", caller.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("selected", caller.RootElement.GetProperty("brainId").GetString());
    }

    [Fact]
    public async Task McpWithoutAnExplicitPostureRefusesToStart()
        => await Assert.ThrowsAsync<OptionsValidationException>(() => Start(null));

    [Fact]
    public async Task MembershipAndBrainIdValidationApplyBeforeToolDispatch()
    {
        await using var app = await Start("Open", deny: true);
        using var client = Client(app);
        using var denied = await Call(client, "/brains/other/mcp");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalid = await Call(client, "/brains/" + new string('x', 201) + "/mcp");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static async Task<WebApplication> Start(string? posture, bool deny = false, bool credentials = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = posture;
        if (credentials)
        {
            builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))] = "operator";
            builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))] = "test-password";
        }
        // No silo here: the kernel answers with no brains, so neurons_context shows the stamp alone.
        builder.Services.AddSingleton<IKernelView>(new FixedKernel());
        builder.Services.AddDigitalBrainMcp();
        builder.Services.AddSingleton<IBrainAccess>(new Access(!deny));
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAccountSession();
        app.MapDigitalBrainMcp();
        try { await app.StartAsync(TestContext.Current.CancellationToken); }
        catch { await app.DisposeAsync(); throw; }
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        return client;
    }

    private static Task<HttpResponseMessage> Call(HttpClient client, string path) => client.PostAsJsonAsync(path,
        new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "neurons_context", arguments = new { } } },
        TestContext.Current.CancellationToken);

    private sealed class Access(bool allow) : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => new(allow);
    }
}
