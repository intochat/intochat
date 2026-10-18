using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Testing;

// A provider's OAuth token endpoint (and Google's profile endpoint) on loopback, reached by the real token exchange
// through DigitalBrain:Connections:TokenEndpoints:{connector}. A code grant answers a long-lived token pair; a refresh
// answers like Google: a new access token and no refresh token. State is per instance, so parallel facts own their stub.
public sealed class TokenEndpointStub : IAsyncDisposable
{
    // A refresh token the provider has revoked: refreshing with it answers invalid_grant.
    public const string RevokedRefreshToken = "revoked-refresh-token";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _forms = new();

    private TokenEndpointStub(WebApplication app, Uri origin)
    {
        _app = app;
        Origin = origin;
        TokenEndpoint = new Uri(origin, "token");
    }

    public Uri Origin { get; }
    public Uri TokenEndpoint { get; }

    // Every form posted to /token, in order, so facts can assert the code, the client secret, PKCE and refreshes.
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Forms => [.. _forms];
    public IReadOnlyDictionary<string, string>? LastForm => _forms.LastOrDefault();
    public string[] RefreshTokens => [.. _forms.Where(IsRefresh).Select(form => form["refresh_token"])];
    public int Refreshes => _forms.Count(IsRefresh);

    // The scope every token answers with.
    public string Scope { get; set; } = "read";

    // Seconds a refreshed access token lives; under a connection's 30-second lead every use refreshes again.
    public int RefreshExpiresIn { get; set; } = 3600;

    // What /profile answers; facts flip it to make the provider refuse or fail.
    public int ProfileStatus { get; set; } = StatusCodes.Status200OK;
    public string? LastProfileAuthorization { get; private set; }

    public static async Task<TokenEndpointStub> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        TestLogging.Apply(builder.Configuration);
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
        app.MapPost("/token", async (HttpRequest request) => stub!.Answer((await request.ReadFormAsync()).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)));
        await app.StartAsync(cancellationToken);
        stub = new TokenEndpointStub(app, new Uri(app.Urls.Single() + "/"));
        return stub;
    }

    private IResult Answer(Dictionary<string, string> form)
    {
        _forms.Enqueue(form);
        if (!IsRefresh(form))
        { return Results.Json(new { token_type = "Bearer", access_token = "integration-access-token", refresh_token = "integration-refresh-token", scope = Scope, expires_in = 3600 }); }
        if (form["refresh_token"] == RevokedRefreshToken)
        { return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest); }
        return Results.Json(new { token_type = "Bearer", access_token = $"access-{Refreshes}", scope = Scope, expires_in = RefreshExpiresIn });
    }

    private static bool IsRefresh(IReadOnlyDictionary<string, string> form) => form.TryGetValue("grant_type", out var grant) && grant == "refresh_token";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
