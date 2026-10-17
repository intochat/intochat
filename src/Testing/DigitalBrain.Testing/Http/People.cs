using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalBrain.Testing;

// Requests on the product routes; failures throw so no assertion library leaks in.
public static class People
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> Send(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{method} {path} returned {(int)response.StatusCode}: {text}");
        }
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
