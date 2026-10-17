using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

// A provider's token endpoint on loopback, reached by the real OAuth token exchange through
// DigitalBrain:Connections:TokenEndpoints:{connector}. A code grant answers a long-lived token pair; a refresh
// answers like Google: a new access token about to lapse and no refresh token.
internal sealed class TokenEndpointStub(WebApplication app, Uri origin, Uri tokenEndpoint, ConcurrentQueue<Dictionary<string, string>> forms) : IAsyncDisposable
{
    public Uri Origin => origin;
    public Uri TokenEndpoint => tokenEndpoint;

    // Every form posted to /token, in order, so facts can assert the code, the client secret, PKCE and refreshes.
    public IReadOnlyList<Dictionary<string, string>> Forms => [.. forms];

    public string[] RefreshTokens => [.. forms.Where(form => form["grant_type"] == "refresh_token").Select(form => form["refresh_token"])];

    public static async Task<TokenEndpointStub> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        TestLogging.Apply(builder.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var forms = new ConcurrentQueue<Dictionary<string, string>>();
        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = (await request.ReadFormAsync()).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            forms.Enqueue(form);
            if (form["grant_type"] == "refresh_token")
            {
                var refreshes = forms.Count(posted => posted["grant_type"] == "refresh_token");
                return Results.Json(new { token_type = "Bearer", access_token = $"access-{refreshes}", scope = "read", expires_in = 1 });
            }
            return Results.Json(new { token_type = "Bearer", access_token = "integration-access-token", refresh_token = "integration-refresh-token", scope = "read", expires_in = 3600 });
        });
        await app.StartAsync(cancellationToken);
        var origin = new Uri(app.Urls.Single() + "/");
        return new TokenEndpointStub(app, origin, new Uri(origin, "token"), forms);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
