namespace IntoChat.OS.Modules.Web.Search;

internal interface ISearchAdapter
{
    SearchProvider Provider { get; }
    Task<WebResult[]> Search(string query, int count, CancellationToken ct);
}

internal sealed record SearchAdapterContext(
    Uri? SearxngUrl,
    Func<string, CancellationToken, Task<string?>> ResolveKey);

internal abstract class SearchAdapterRegistration(SearchProvider provider)
{
    public SearchProvider Provider { get; } = provider;
    public abstract ISearchAdapter Create(SearchAdapterContext context);
}
