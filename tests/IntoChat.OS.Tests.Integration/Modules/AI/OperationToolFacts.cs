using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.AI;
using IntoChat.OS.Tests.Support.Fakes.Postgres;

namespace IntoChat.OS.Tests.Integration.Modules.AI;

public sealed class OperationToolFacts
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task InoResearchesACompanyByCallingTheAppsOperation(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.CallsTool("customer_researcher_research", new { companyName = "Globex", industry = "Energy", fitScore = 70 }))
            .Turn(t => t.Says("Saved Globex."));
        await using var os = await Journey.Start(kind, f => f.Postgres().WebSearch().Llm(script), ct);
        var alice = os.Person("alice");
        await alice.OpenInWorkspace("customer-researcher", ct);

        await alice.Ino().Say("Research Globex, an energy company.", ct);
        await os.Idle();

        script.ShouldBeFinished();
        var call = Assert.Single(script.Calls);
        Assert.Equal("customer_researcher_research", call.Name);
        var rows = os.Fake<FakeTableStore>().RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey);
        Assert.Contains(rows, r => r.Any(v => v.Json == "\"Globex\""));
    }
}
