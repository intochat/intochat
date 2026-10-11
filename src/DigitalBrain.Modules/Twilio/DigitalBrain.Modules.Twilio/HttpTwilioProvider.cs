using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Twilio;

// Twilio's REST API: POST 2010-04-01/Accounts/{sid}/Messages.json, form-encoded, basic auth sid:token.
internal sealed class HttpTwilioProvider(IHttpClientFactory clients, TwilioModuleOptions options) : ITwilioProvider
{
    private readonly Uri _baseUrl = options.ResolveBaseUrl();

    public async Task<TwilioSendResult> SendAsync(TwilioAccount account, string to, string body, Uri? statusCallback, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>> { new("To", to), new("From", account.From), new("Body", body) };
        if (statusCallback is not null) { fields.Add(new("StatusCallback", statusCallback.AbsoluteUri)); }
        using var request = Request(HttpMethod.Post, account, $"2010-04-01/Accounts/{Uri.EscapeDataString(account.AccountSid)}/Messages.json");
        request.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var client = clients.CreateClient(TwilioModule.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            var json = await ReadJsonAsync(response, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return Text(json, "sid") is { } sid
                    ? new(TwilioOutcome.Accepted, sid, Text(json, "status") ?? "queued", Text(json, "error_code"), "accepted")
                    : new(TwilioOutcome.Unavailable, null, "failed", null, $"Twilio answered {status} without a message sid");
            }
            var code = Text(json, "code");
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new(TwilioOutcome.Unauthorized, null, "failed", code, $"unauthorized ({status})"),
                _ when status >= 500 => new(TwilioOutcome.Unavailable, null, "failed", code, $"Twilio is unavailable ({status})"),
                _ => new(TwilioOutcome.Refused, null, "failed", code, code is null ? $"refused ({status})" : $"refused ({status}, Twilio error {code})"),
            };
        }
        catch (HttpRequestException) { return new(TwilioOutcome.Unavailable, null, "failed", null, "Twilio is unreachable"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(TwilioOutcome.Unavailable, null, "failed", null, "Twilio timed out"); }
    }

    public async Task<TwilioOutcome> ProbeAsync(TwilioAccount account, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, account, $"2010-04-01/Accounts/{Uri.EscapeDataString(account.AccountSid)}.json");
        try
        {
            using var client = clients.CreateClient(TwilioModule.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? TwilioOutcome.Accepted
                : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? TwilioOutcome.Unauthorized
                : TwilioOutcome.Unavailable;
        }
        catch (HttpRequestException) { return TwilioOutcome.Unavailable; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return TwilioOutcome.Unavailable; }
    }

    private HttpRequestMessage Request(HttpMethod method, TwilioAccount account, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.AccountSid}:{account.AuthToken}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    // Twilio sends error_code and code as numbers; either shape reads as text.
    private static string? Text(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null }
            : null;
}
