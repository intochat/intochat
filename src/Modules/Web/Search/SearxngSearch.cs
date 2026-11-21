using System.Net.Http.Headers;
using System.Text.Json;

namespace IntoChat.OS.Modules.Web.Search;

internal sealed class SearxngSearch(HttpClient client, Uri baseUrl) : ISearchAdapter
{
    public SearchProvider Provider => SearchProvider.Searxng;

    public async Task<WebResult[]> Search(string query, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var endpoint = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + $"/search?q={Uri.EscapeDataString(query)}&format=json&safesearch=1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync(stream, WebJson.Default.SearxngResponse, ct).ConfigureAwait(false);
        if (payload?.Results is not { Length: > 0 } items)
        {
            return [];
        }

        return SearchResults.Normalize(items.Select(static r => (r.Title, r.Url, r.Content)), count);
    }
}
