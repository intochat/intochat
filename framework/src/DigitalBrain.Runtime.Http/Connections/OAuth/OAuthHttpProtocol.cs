using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

/// <summary>Bounded HTTP response returned by <see cref="OAuthHttpProtocol.PostFormAsync"/>.</summary>
public readonly record struct OAuthHttpResponse(HttpStatusCode StatusCode, string Body)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;
}

/// <summary>
/// Shared OAuth 2.0 HTTP transport and strict token-response parser used by both
/// Authorization Code (<see cref="OAuthTokenExchange"/>) and Device Authorization (<see cref="OAuthDeviceFlow"/>).
/// </summary>
public static class OAuthHttpProtocol
{
    public const int MaxResponseBytes = 256 * 1024;
    public const int MaxTokenLength = 16384;
    public const double MaxExpiresInSeconds = 315360000;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    public static async Task<OAuthHttpResponse> PostFormAsync(
        IHttpClientFactory clients,
        string clientName,
        Uri endpoint,
        IReadOnlyDictionary<string, string> form,
        TimeProvider clock,
        Action<HttpRequestMessage>? configureRequest = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(clock);

        using var client = clients.CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        configureRequest?.Invoke(request);

        using var deadline = new CancellationTokenSource(DefaultRequestTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
            return new OAuthHttpResponse(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
        catch (HttpRequestException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
        catch (IOException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
    }

    public static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) { break; }
            if (output.Length + count > MaxResponseBytes)
            {
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            output.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public static JsonDocument ParseJsonObject(string body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            return document;
        }
        catch (JsonException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }
    }

    public static string ReadTokenField(JsonElement root, string property)
    {
        var value = root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTokenLength || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        return value;
    }

    public static OAuthGrant ParseTokenGrant(
        JsonElement root,
        string rawBody,
        string authMethodId,
        IEnumerable<string> defaultScopes,
        string? target,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(defaultScopes);
        ArgumentNullException.ThrowIfNull(clock);

        var accessToken = ReadTokenField(root, "access_token");
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshProp) && refreshProp.ValueKind == JsonValueKind.String
            ? refreshProp.GetString()
            : null;
        if (refreshToken is not null && (refreshToken.Length > MaxTokenLength || refreshToken.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresProp)
            && double.TryParse(expiresProp.ToString(), CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < MaxExpiresInSeconds
            ? clock.GetUtcNow().AddSeconds(seconds)
            : null;

        var scopes = string.Join(' ', defaultScopes);
        if (root.TryGetProperty("scope", out var scopeProp))
        {
            if (scopeProp.ValueKind != JsonValueKind.String
                || scopeProp.GetString() is not { Length: <= MaxTokenLength } returnedScopes
                || returnedScopes.Any(char.IsControl))
            {
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            scopes = returnedScopes;
        }

        return new OAuthGrant(authMethodId, accessToken, refreshToken, expiresAt, scopes, target, rawBody);
    }
}
