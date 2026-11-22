using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Web;

namespace IntoChat.OS.Tests.Integration.Modules.Web;

public sealed class WebSearchThroughAdapterFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task SeededQueryReturnsSeededResultAndRecordsQuery(NeuronRuntimeKind kind)
    {
        var expected = new WebResult("Acme", new Uri("https://acme.test"), "Acme moves freight.");
        await using var os = await Journey.Start(
            kind,
            f => f.WebSearch(s => s.Answer("acme", expected)),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var search = ada.Get<IWebSearch>(new NeuronId(ada.Brain, "web.search", IWebSearch.DefaultKey));

        var results = await search.Search("acme company overview", 3);

        var only = Assert.Single(results);
        Assert.Equal(expected, only);

        var adapter = os.Fake<FakeSearchAdapter>();
        var query = Assert.Single(adapter.Queries);
        Assert.Equal("acme company overview", query);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task UnseededQueryReturnsEmptyArray(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(
            kind,
            f => f.WebSearch(s => s.Answer("acme", new WebResult("Acme", new Uri("https://acme.test"), "Acme moves freight."))),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var search = ada.Get<IWebSearch>(new NeuronId(ada.Brain, "web.search", IWebSearch.DefaultKey));

        var results = await search.Search("unrelated topic", 3);

        Assert.Empty(results);
        var adapter = os.Fake<FakeSearchAdapter>();
        Assert.Equal(["unrelated topic"], adapter.Queries);
    }
}
