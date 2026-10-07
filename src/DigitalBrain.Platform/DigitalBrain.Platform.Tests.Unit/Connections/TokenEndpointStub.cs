using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

internal sealed class TokenEndpointStub(WebApplication app, Uri origin, Uri tokenEndpoint) : IAsyncDisposable
{
    public Uri Origin => origin;
    public Uri TokenEndpoint => tokenEndpoint;

    // The last form posted to /token, so facts can assert the code, the client secret and PKCE.
    public static Dictionary<string, string>? LastForm { get; private set; }

    public static async Task<TokenEndpointStub> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/profile", () => Results.Json(new { emailAddress = "user@gmail.com" }));
        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            LastForm = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            return Results.Json(new { token_type = "Bearer", access_token = "integration-access-token", refresh_token = "integration-refresh-token", scope = "read", expires_in = 3600 });
        });
        await app.StartAsync(cancellationToken);
        var origin = new Uri(app.Urls.Single() + "/");
        return new TokenEndpointStub(app, origin, new Uri(origin, "token"));
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
