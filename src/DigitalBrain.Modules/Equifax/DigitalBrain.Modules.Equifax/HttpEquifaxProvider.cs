using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Equifax;

// ASSUMED shape (see README): POST {BaseUrl}v2/oauth/token with client_credentials and basic auth,
// then GET {BaseUrl}business/consumer-credit/v1/reports?documentType=DNI&documentNumber=… with the bearer.
internal sealed class HttpEquifaxProvider(IHttpClientFactory clients, TimeProvider time, EquifaxModuleOptions options) : IEquifaxProvider
{
    private static readonly TimeSpan ExpiryLead = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public async Task AcquireTokenAsync(EquifaxCredential credential, CancellationToken ct)
        => _ = await TokenAsync(credential, ct);

    public async Task<string> ReadReportAsync(EquifaxCredential credential, string documentNumber, CancellationToken ct)
    {
        var token = await TokenAsync(credential, ct);
        var uri = new Uri(credential.BaseUrl, $"business/consumer-credit/v1/reports?documentType=DNI&documentNumber={Uri.EscapeDataString(documentNumber)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { _tokens.TryRemove(CacheKey(credential), out _); }
        Ensure(response);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> TokenAsync(EquifaxCredential credential, CancellationToken ct)
    {
        var key = CacheKey(credential);
        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresAt - ExpiryLead > time.GetUtcNow()) { return cached.Token; }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(credential.BaseUrl, "v2/oauth/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", options.Scope)]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.ClientId}:{credential.ClientSecret}")));
        using var response = await SendAsync(request, ct);
        Ensure(response);
        string token;
        int lifetime;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            token = json.RootElement.GetProperty("access_token").GetString() ?? throw new EquifaxProviderException("invalid token response");
            lifetime = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 300;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new EquifaxProviderException("invalid token response"); }
        _tokens[key] = (token, time.GetUtcNow().AddSeconds(lifetime));
        return token;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await clients.CreateClient(EquifaxModule.HttpClientName).SendAsync(request, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new EquifaxProviderException("timeout"); }
        catch (HttpRequestException) { throw new EquifaxProviderException("unreachable"); }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) { return; }
        throw new EquifaxProviderException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthorized (401)",
            HttpStatusCode.Forbidden => "forbidden (403)",
            HttpStatusCode.NotFound => "not found (404)",
            var status => ((int)status).ToString(CultureInfo.InvariantCulture),
        });
    }

    // The secret enters the key only as a hash, so a rotated secret gets a fresh token.
    private static string CacheKey(EquifaxCredential credential)
        => $"{credential.BaseUrl.AbsoluteUri}|{credential.ClientId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.ClientSecret)))}";
}
