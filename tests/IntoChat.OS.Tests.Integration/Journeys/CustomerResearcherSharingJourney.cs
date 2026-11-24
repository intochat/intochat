using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.AI;
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

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AliceSharesHerCustomersWithBob(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.CallsTool("customer_researcher_research", new { companyName = "Globex", industry = "Energy", fitScore = 70 }))
            .Turn(t => t.Says("Saved Globex."));
        await using var os = await Journey.Start(
            kind,
            f => f.Postgres()
                  .WebSearch(w => w.Answer("Acme", new WebResult("Acme", new("https://acme.test"), "Acme moves freight.")))
                  .Llm(script),
            ct);
        var alice = os.Person("alice");
        var bob = os.Person("bob");
        await alice.Befriend(bob, ct);

        // Alice uses her own app; Ino adds a second company through the tool.
        await alice.OpenInWorkspace("customer-researcher", ct);
        await using var hers = await alice.Open<CustomerResearcherApp>(ct: ct);
        await hers.Type("input:company", "Acme", ct);
        await hers.Click("btn:research", ct);
        await alice.Ino().Say("Research Globex, an energy company.", ct);
        await os.Idle();

        // Bob's own installation is empty.
        await using (var his = await bob.Open<CustomerResearcherApp>(ct: ct))
        {
            his.ShouldDisplayText("No companies researched yet");
        }

        // Alice offers her table; Bob binds his installation to it.
        var aliceTable = new NeuronId(alice.Brain, "postgres.table", CustomerResearcherApp.ProfilesTableKey);
        await alice.Offers.Offer(aliceTable, bob.Account, Role.Viewer, ct);
        await bob.Install<CustomerResearcherApp>(ct, new Binding("customers", new BindingTarget.Offered(alice.Brain, aliceTable)));

        await using var shared = await bob.Open<CustomerResearcherApp>(ct: ct);
        shared.ShouldDisplayText("Acme");
        shared.ShouldDisplayText("Globex");
        shared.ShouldNotHaveEnabled("btn:research"); // read-only

        // Revoked while open (Review focus 1): the next refresh is refused and shown.
        await alice.Offers.Revoke(aliceTable, bob.Account, ct);
        await shared.Send(new RefreshProfiles(), ct);
        shared.ShouldDisplayText("no longer shared");
        shared.ShouldNotDisplayText("Acme");

        Assert.Equal(2, os.Fake<FakeTableStore>().RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey).Length);
        script.ShouldBeFinished();
    }

    [Fact]
    public async Task CustomerResearcherSharingJourney_TestOS_CompletesWithinBudget()
    {
        const long budgetMs = 3_000;
        var sw = Stopwatch.StartNew();
        await AliceSharesHerCustomersWithBob(NeuronRuntimeKind.TestOS);
        sw.Stop();

        Assert.True(
            sw.ElapsedMilliseconds < budgetMs,
            $"TestOS sharing journey took {sw.ElapsedMilliseconds}ms, exceeding the {budgetMs}ms budget.");
    }
}
