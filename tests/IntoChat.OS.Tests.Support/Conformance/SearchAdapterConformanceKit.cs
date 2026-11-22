using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;
using Xunit;

namespace IntoChat.OS.Tests.Support.Conformance;

/// <summary>
/// Conformance kit for the Web <c>ISearchAdapter</c> outside edge: verifies seeded queries return seeded
/// <see cref="WebResult"/> values, count limits are respected, and unseeded queries return an empty array.
/// </summary>
public abstract class SearchAdapterConformanceKit
{
    /// <summary>Creates a search adapter seeded with the given <c>(queryContains, results)</c> pairs.</summary>
    internal abstract ISearchAdapter CreateAdapter(params (string QueryContains, WebResult[] Results)[] seeded);

    /// <summary>A query matching a seeded substring returns the seeded results.</summary>
    [Fact]
    public async Task SeededQueryReturnsSeededWebResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new[]
        {
            new WebResult("Acme Corp", new Uri("https://acme.test"), "Global freight logistics."),
            new WebResult("Acme News", new Uri("https://news.acme.test"), "Quarterly update."),
        };
        var adapter = CreateAdapter(("acme", expected));

        var actual = await adapter.Search("Acme Corp logistics", count: 5, ct).ConfigureAwait(false);

        Assert.Equal(expected, actual);
    }

    /// <summary>The adapter caps returned results to the requested count limit.</summary>
    [Fact]
    public async Task CountLimitIsRespected()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new[]
        {
            new WebResult("One", new Uri("https://one.test"), "First"),
            new WebResult("Two", new Uri("https://two.test"), "Second"),
            new WebResult("Three", new Uri("https://three.test"), "Third"),
            new WebResult("Four", new Uri("https://four.test"), "Fourth"),
        };
        var adapter = CreateAdapter(("widgets", seeded));

        var actual = await adapter.Search("widgets", count: 2, ct).ConfigureAwait(false);

        Assert.Equal(2, actual.Length);
        Assert.Equal(seeded[..2], actual);
    }

    /// <summary>An unseeded query returns an empty array rather than throwing.</summary>
    [Fact]
    public async Task UnseededQueryReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = CreateAdapter(("acme", [new WebResult("Acme", new Uri("https://acme.test"), "Freight.")]));

        var actual = await adapter.Search("unmatched-company-query", count: 5, ct).ConfigureAwait(false);

        Assert.Empty(actual);
    }
}
