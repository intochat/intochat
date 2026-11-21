using System.Text.Json.Serialization;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Modules.Web.Search;

internal sealed record SearxngResponse(
    [property: JsonPropertyName("results")] SearxngItem[]? Results);

internal sealed record SearxngItem(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("content")] string? Content);

internal sealed record BraveResponse(
    [property: JsonPropertyName("web")] BraveWebSection? Web);

internal sealed record BraveWebSection(
    [property: JsonPropertyName("results")] BraveItem[]? Results);

internal sealed record BraveItem(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("description")] string? Description);

internal sealed record TavilyRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("max_results")] int MaxResults);

internal sealed record TavilyResponse(
    [property: JsonPropertyName("results")] TavilyItem[]? Results);

internal sealed record TavilyItem(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("content")] string? Content);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearxngResponse))]
[JsonSerializable(typeof(BraveResponse))]
[JsonSerializable(typeof(TavilyRequest))]
[JsonSerializable(typeof(TavilyResponse))]
internal sealed partial class WebJson : JsonSerializerContext;

internal static class SearchResults
{
    private const int MaxSnippetChars = 500;

    public static WebResult[] Normalize(IEnumerable<(string? Title, string? Url, string? Snippet)> items, int maxCount)
    {
        var results = new List<WebResult>();
        foreach (var (rawTitle, rawUrl, rawSnippet) in items)
        {
            if (results.Count >= maxCount)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(rawUrl)
                || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(url.Host))
            {
                continue;
            }

            var title = UntrustedText.Clean((rawTitle ?? string.Empty).Trim());
            var snippet = UntrustedText.Clean((rawSnippet ?? string.Empty).Trim());
            if (snippet.Length > MaxSnippetChars)
            {
                var cut = char.IsHighSurrogate(snippet[MaxSnippetChars - 1]) ? MaxSnippetChars - 1 : MaxSnippetChars;
                snippet = snippet[..cut];
            }

            results.Add(new WebResult(title, url, snippet));
        }

        return [.. results];
    }
}
