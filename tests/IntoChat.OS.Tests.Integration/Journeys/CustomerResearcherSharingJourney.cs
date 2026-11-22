using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;

namespace IntoChat.OS.Tests.Integration.Journeys;

public sealed class CustomerResearcherSharingJourney
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ResearchingACompanyStoresItsProfileInTheOwnersTable(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres(), ct);
        var alice = os.Person("alice");
        await using var app = await alice.Open<CustomerResearcherApp>(ct: ct);

        await app.Send(new ResearchCompany("Acme Corp", "Logistics", "A logistics company.", 92), ct);

        app.ShouldDisplayText("Acme Corp");
        Assert.Single(os.Fake<FakeTableStore>().RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey));
    }
}
