using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Modules.Web.Search;

internal sealed class TavilySearch(HttpClient client, Func<CancellationToken, Task<string?>> getKey) : ISearchAdapter
{
    private static readonly Uri Endpoint = new("https://api.tavily.com/search");

    public TavilySearch(HttpClient client, Func<Task<string?>> getKey)
        : this(client, _ => getKey())
    {
    }

    public SearchProvider Provider => SearchProvider.Tavily;

    public async Task<WebResult[]> Search(string query, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var key = await getKey(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Tavily needs an API key: Settings → API keys → Tavily.");
        }

        var body = JsonSerializer.Serialize(new TavilyRequest(query, count), WebJson.Default.TavilyRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync(stream, WebJson.Default.TavilyResponse, ct).ConfigureAwait(false);
        if (payload?.Results is not { Length: > 0 } items)
        {
            return [];
        }

        return SearchResults.Normalize(items.Select(static r => (r.Title, r.Url, r.Content)), count);
    }
}
