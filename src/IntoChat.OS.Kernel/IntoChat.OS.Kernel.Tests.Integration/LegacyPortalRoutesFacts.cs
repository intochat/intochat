using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Spi;
using IntoChat.Cloud.Auth;
using IntoChat.Cloud.Lobby;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class LegacyPortalRoutesFacts
{
    [Fact]
    public async Task LegacyPortalPagesAreGoneAndTheLobbySessionIsAnonymous()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
        builder.Services.AddSingleton<ILobbyTokenKey>(new FixedKey());
        builder.Services.AddSingleton<INeuronProxies>(new UnusedProxies());
        builder.Services.AddSingleton<ISessionIssuer>(new UnusedIssuer());
        new LobbyModule().ConfigureServices(builder.Services);
        var app = builder.Build();
        LobbyModule.MapHttp(app);
        var auth = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "IntoChat.Cloud", "Modules", "Auth"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Http{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Aggregate((left, right) => left + right);
        foreach (var path in new[] { "/auth/entry", "/auth/login", "/auth/signup", "/auth/password-reset", "/auth/totp", "/account/profile" })
        {
            Assert.DoesNotContain($"MapGet(\"{path}\"", auth, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/auth/entry", "/account/profile", "/portal/assets", "/portal/totp/qr" })
        {
            Assert.DoesNotContain($"\"{path}\"", auth, StringComparison.Ordinal);
        }

        Assert.Contains("MapPost(\"/auth/login\"", auth, StringComparison.Ordinal);

        foreach (var path in new[] { "/auth/entry", "/auth/login", "/auth/signup", "/auth/password-reset", "/auth/totp", "/account/profile", "/portal/assets/auth.js" })
        {
            Assert.Null(Find(app, HttpMethods.Get, path));
        }

        Assert.Null(Find(app, HttpMethods.Post, "/portal/totp/qr"));

        var session = Find(app, HttpMethods.Get, "/lobby/session");
        Assert.NotNull(session);
        Assert.NotNull(session!.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/lobby/session";
        context.RequestServices = app.Services;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(session);
        await session.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("lobby").GetBoolean());
    }

    private static RouteEndpoint? Find(IEndpointRouteBuilder app, string method, string path)
        => app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .FirstOrDefault(endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) != false);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class FixedKey : ILobbyTokenKey
    {
        public byte[] Key { get; } = new byte[32];
    }

    private sealed class UnusedProxies : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => throw new NotSupportedException();
    }

    private sealed class UnusedIssuer : ISessionIssuer
    {
        public Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EndAsync(HttpContext http, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
