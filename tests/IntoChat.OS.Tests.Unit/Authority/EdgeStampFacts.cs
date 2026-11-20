using System.Net;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Unit.Authority;

// The HTTP and MCP edges stamp the caller through EdgeAuthority, and a request with no caller is refused, never a 500.
public sealed class EdgeStampFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpCallerIsAnAgentForTheSessionPerson()
    {
        await using var os = TestOS.Local();
        var proof = new PresenceProofValidator(RandomNumberGenerator.GetBytes(32));
        var services = new ServiceCollection().AddSingleton<IPresenceProof>(proof).AddSingleton(os.Edge).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers[PresenceProof.HeaderName] = proof.Token;
        var mcp = new McpCaller(new HttpContextAccessor { HttpContext = http }, Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), os, os.Edge);

        var stamped = mcp.Stamp(NeuronSurfaceTools.CallName);

        var agent = Assert.IsType<Actor.Agent>(stamped.Actor);
        Assert.Equal(new Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin), agent.For);
        Assert.Equal(new BrainId(AccountSession.DefaultLogin), stamped.Brain);
        Assert.Equal("mcp", stamped.Conversation);
        Assert.Same(stamped, os.Callers.Current);
        // It reaches the owner brain for its person, and is refused what only the person directly may do.
        Assert.Equal(AccountSession.DefaultLogin, CallerRules.ActsFor(stamped));
        Assert.False(CallerRules.IsDirectPerson(stamped, AccountSession.DefaultLogin));
        Assert.False(CallerRules.IsApp(stamped, out _));
    }

    [Fact]
    public async Task UnauthenticatedRequestIsRefusedNot500()
    {
        await using var os = TestOS.Local();
        await using var app = await StartAsync(os, IdentityPosture.Secured, username: null, password: null);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var anonymous = await client.GetAsync("/probe/caller", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/probe/caller");
        forged.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("owner:wrong")));
        using var forgedReply = await client.SendAsync(forged, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedReply.StatusCode);

        // MCP outside an authenticated request refuses with UnauthorizedAccessException, which the edge answers as 401.
        var services = new ServiceCollection().AddSingleton(os.Edge).BuildServiceProvider();
        var mcp = new McpCaller(new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = services } },
            Options.Create(new AuthOptions { Posture = IdentityPosture.Secured }), os, os.Edge);
        Assert.Throws<UnauthorizedAccessException>(() => mcp.Stamp(NeuronSurfaceTools.CallName));
    }

    [Fact]
    public async Task BasicOwnerLoginStampsTheDirectOwner()
    {
        await using var os = TestOS.Local();
        await using var app = await StartAsync(os, IdentityPosture.Secured, AccountSession.DefaultLogin, "s3cret-owner");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe/caller");
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(AccountSession.DefaultLogin + ":s3cret-owner")));

        using var reply = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal($"Person|{AccountSession.DefaultLogin}|{AccountSession.DefaultLogin}|{AccountSession.DefaultLogin}|True", await reply.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<WebApplication> StartAsync(TestOS os, IdentityPosture posture, string? username, string? password)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(EdgeStampFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthKeys.Posture] = posture.ToString(),
            [AuthKeys.Username] = username,
            [AuthKeys.Password] = password,
        });
        builder.Services.AddSingleton(os.Edge);
        builder.Services.AddSingleton(os.Callers);
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthKeys.SectionName);

        var app = builder.Build();
        app.UseAccountSession();
        app.MapGet("/probe/caller", (ICallerAmbient callers) =>
        {
            var caller = callers.Current ?? throw new UnauthorizedAccessException("no caller");
            var person = caller.For!;
            return Results.Text($"{caller.Actor.GetType().Name}|{person.Principal}|{person.Account}|{caller.Brain.Value}|{caller.Direct}");
        });
        await app.StartAsync(Ct);
        return app;
    }
}
