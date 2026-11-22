using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;
using IntoChat.OS.Tests.Support.Conformance;
using IntoChat.OS.Tests.Support.Fakes.AI;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using IntoChat.OS.Tests.Support.Fakes.Web;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Conformance;

/// <summary>
/// Runs the outside-edge conformance kits against the in-memory fakes (<see cref="FakeTableStore"/>,
/// <see cref="FakeSearchAdapter"/>, and <see cref="ScriptedChatClient"/>).
/// </summary>
public static class EdgeConformanceFacts
{
    public sealed class FakeTableStoreFacts : TableStoreConformanceKit
    {
        internal override ITableStore CreateStore() => new FakeTableStore();
    }

    public sealed class FakeSearchAdapterFacts : SearchAdapterConformanceKit
    {
        internal override ISearchAdapter CreateAdapter(params (string QueryContains, WebResult[] Results)[] seeded)
        {
            var adapter = new FakeSearchAdapter();
            foreach (var (queryContains, results) in seeded)
            {
                adapter.Answer(queryContains, results);
            }
            return adapter;
        }
    }

    public sealed class ScriptedChatClientFacts : ChatClientConformanceKit
    {
        protected override IChatClient CreateClient(LlmScript script) => new ScriptedChatClient(script);
    }
}
