using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;

namespace IntoChat.OS.Tests.Support.Fakes.Web;

/// <summary>
/// In-memory <see cref="ISearchAdapter"/> fake that answers queries by case-insensitive substring match
/// and records every query received.
/// </summary>
public sealed class FakeSearchAdapter : ISearchAdapter
{
    private readonly Lock _gate = new();
    private readonly List<(string QueryContains, WebResult[] Results)> _answers = [];
    private readonly List<string> _queries = [];

    SearchProvider ISearchAdapter.Provider => SearchProvider.Searxng;

    /// <summary>Every search query received by this fake, in call order.</summary>
    public IReadOnlyList<string> Queries
    {
        get
        {
            lock (_gate)
            {
                return [.. _queries];
            }
        }
    }

    /// <summary>Seeds results returned whenever a query contains <paramref name="queryContains"/> (case-insensitive).</summary>
    public FakeSearchAdapter Answer(string queryContains, params WebResult[] results)
    {
        ArgumentNullException.ThrowIfNull(queryContains);
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            _answers.Add((queryContains, [.. results]));
        }
        return this;
    }

    Task<WebResult[]> ISearchAdapter.Search(string query, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        lock (_gate)
        {
            _queries.Add(query);
            foreach (var (queryContains, results) in _answers)
            {
                if (query.Contains(queryContains, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(results.Take(count).ToArray());
                }
            }
        }

        return Task.FromResult<WebResult[]>([]);
    }
}
