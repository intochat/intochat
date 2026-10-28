using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace IntoChat.Cloud.Auth.Tests;

internal sealed class ScriptedGoogleBackchannel : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> requests = new();

    public string UserInfoJson { get; set; } =
        JsonSerializer.Serialize(new { sub = "google-sub-1", email = "spike@example.com", email_verified = true, name = "Spike User" });

    public IReadOnlyList<string> Requests => [.. requests];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Enqueue($"{request.Method} {request.RequestUri}");
        var isToken = request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal);
        var json = isToken
            ? "{\"access_token\":\"scripted-access-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}"
            : UserInfoJson;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}
