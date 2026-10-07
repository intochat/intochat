using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

// Stands in for Google's token and profile endpoints. State is per instance: facts in other
// collections run in parallel against their own stub.
internal sealed class TokenEndpointStub : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TokenEndpointStub(WebApplication app, Uri origin)
    {
        _app = app;
        Origin = origin;
        TokenEndpoint = new Uri(origin, "token");
    }

    public Uri Origin { get; }
    public Uri TokenEndpoint { get; }

    // The last form posted to /token, so facts can assert the grant type and the refresh token.
    public Dictionary<string, string>? LastForm { get; private set; }

    // What /profile answers; facts flip it to make Google refuse or fail.
    public int ProfileStatus { get; set; } = StatusCodes.Status200OK;
    public string? LastProfileAuthorization { get; private set; }

    // A refresh token Google has revoked: refreshing with it answers invalid_grant.
    public const string RevokedRefreshToken = "revoked-refresh-token";

    // Seconds a refreshed access token lives; under the base's 30-second lead every use refreshes again.
    public int RefreshExpiresIn { get; set; } = 3600;
    private int _refreshes;
    public int Refreshes => Volatile.Read(ref _refreshes);

    public static async Task<TokenEndpointStub> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TokenEndpointStub? stub = null;
        app.MapGet("/profile", (HttpRequest request) =>
        {
            stub!.LastProfileAuthorization = request.Headers.Authorization.ToString();
            return stub.ProfileStatus == StatusCodes.Status200OK
                ? Results.Json(new { emailAddress = "user@gmail.com" })
                : Results.StatusCode(stub.ProfileStatus);
        });
        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            stub!.LastForm = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            if (form["grant_type"] != "refresh_token")
            { return Results.Json(new { token_type = "Bearer", access_token = "integration-access-token", refresh_token = "integration-refresh-token", scope = "https://www.googleapis.com/auth/gmail.readonly", expires_in = 3600 }); }
            if (form["refresh_token"] == RevokedRefreshToken)
            { return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest); }
            // Like Google, a refresh answers a new access token and no refresh token.
            Interlocked.Increment(ref stub._refreshes);
            return Results.Json(new { token_type = "Bearer", access_token = "refreshed-access-token", scope = "https://www.googleapis.com/auth/gmail.readonly", expires_in = stub.RefreshExpiresIn });
        });
        await app.StartAsync(cancellationToken);
        stub = new TokenEndpointStub(app, new Uri(app.Urls.Single() + "/"));
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
