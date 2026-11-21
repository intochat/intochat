namespace IntoChat.OS.Modules.Web.Search;

internal interface ISearchAdapter
{
    SearchProvider Provider { get; }
    Task<WebResult[]> Search(string query, int count, CancellationToken ct);
}
