namespace IntoChat.OS.Modules.Web;

public enum SearchProvider
{
    Off,
    Searxng,
    Brave,
    Tavily,
    Native,
}

public sealed record WebResult(string Title, Uri Url, string Snippet);

public sealed record WebPage(Uri Url, string Title, string Markdown);
