using System.Net.Http.Headers;
using System.Text.Json;

namespace IntoChat.OS.Modules.Web.Search;

internal sealed class BraveSearch(HttpClient client, Func<CancellationToken, Task<string?>> getKey) : ISearchAdapter
{
    public SearchProvider Provider => SearchProvider.Brave;

    public async Task<WebResult[]> Search(string query, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var key = await getKey(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Brave Search needs an API key: Settings → API keys → Brave.");
        }

        var endpoint = new Uri($"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", key.Trim());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync(stream, WebJson.Default.BraveResponse, ct).ConfigureAwait(false);
        if (payload?.Web?.Results is not { Length: > 0 } items)
        {
            return [];
        }

        return SearchResults.Normalize(items.Select(static r => (r.Title, r.Url, r.Description)), count);
    }
}

internal sealed class BraveSearchRegistration(IHttpClientFactory? clients = null)
    : SearchAdapterRegistration(SearchProvider.Brave)
{
    public override ISearchAdapter Create(SearchAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BraveSearch(WebSearchNeuron.CreateClient(clients), ct => context.ResolveKey(WebIntegrations.BraveId, ct));
    }
}
