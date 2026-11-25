using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class CustomerResearcherRuntimeFixture : BothRuntimesFixture
{
    private readonly FakeSet _fakes = new FakeSet().Postgres().WebSearch();

    protected override IReadOnlyList<Type> Modules => Journey.SliceModules;

    protected override void ConfigureTestOS(IServiceCollection services) => _fakes.Apply(services);

    protected override void ConfigureSilo(IServiceCollection services) => _fakes.Apply(services);
}

public sealed class CustomerResearcherAppContractFacts(CustomerResearcherRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<CustomerResearcherApp, CustomerResearcherRuntimeFixture>(fixture)
{
    protected override string? ConcurrentCommandKey => "btn:clear";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TypingACompanyAndClickingResearchSavesIt(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(
            kind,
            f => f.Postgres().WebSearch(w => w.Answer("Acme", new WebResult("Acme Corp", new Uri("https://acme.example"), "Global industrial robotics leader."))),
            ct);

        var alice = os.Person("alice");
        await using var app = await alice.Open<CustomerResearcherApp>(ct: ct);

        app.ShouldDisplayText("Customer Researcher")
            .ShouldDisplayText("No companies researched yet");

        await app.Type("input:company", "Acme", ct);
        await app.Click("btn:research", ct);

        app.ShouldDisplayText("Acme")
            .ShouldDisplayText("Global industrial robotics leader.");
        Assert.Single(os.Fake<FakeTableStore>().RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ProfilesSurviveReactivationBecauseTheTableIsTheSource(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres().WebSearch(), ct);

        var alice = os.Person("alice");
        await using (var app = await alice.Open<CustomerResearcherApp>("main", ct))
        {
            await app.Send(new ResearchCompany("Acme Corp", "SaaS", "Enterprise workflow automation", 92), ct);
            app.ShouldDisplayText("Acme Corp");
        }

        await os.Runtime.Restart(ct);

        await using var reopened = await alice.Open<CustomerResearcherApp>("main", ct);
        reopened.ShouldDisplayText("Acme Corp")
            .ShouldDisplayText("SaaS")
            .ShouldDisplayText("Enterprise workflow automation");
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ATableFailureIsShownNotSwallowed(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres().WebSearch(), ct);

        var alice = os.Person("alice");
        await using var app = await alice.Open<CustomerResearcherApp>(ct: ct);

        os.Fake<FakeTableStore>().FailNext();

        await app.Send(new ResearchCompany("FailCo", "FinTech", "Payment rails", 85), ct);

        app.ShouldDisplayText("Could not save")
            .ShouldNotDisplayText("FailCo");
        Assert.Empty(os.Fake<FakeTableStore>().RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task StateWrittenBeforeThisChangeStillLoads(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres().WebSearch(), ct);

        var alice = os.Person("alice");
        var screenId = new NeuronId(alice.Brain, "app.customer-researcher", "legacy");
        const string legacyJson = """
            {"Query":"x","Profiles":[{"CompanyName":"LegacyCo","Industry":"Retail","Summary":"Old cached row","FitScore":75,"Status":"Qualified"}],"LastMessage":null}
            """;
        await os.Runtime.SeedState(screenId, legacyJson, ct);

        await using var app = await alice.Open<CustomerResearcherApp>("legacy", ct);

        // The legacy "Query" field deserializes into Draft and is rendered in input:company.
        app.ShouldDisplayText("x");
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TheAppIsInTheCatalogWithItsOperations(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, f => f.Postgres().WebSearch().Catalog(), ct);

        var alice = os.Person("alice");
        var store = alice.Get<IAppStore>(new NeuronId(alice.Brain, "apps.store", IAppStore.Relative));

        var entries = await store.List();
        var listed = Assert.Single(entries, e => e.Name == "customer-researcher");
        Assert.Equal("Customer Researcher", listed.Title);

        var catalog = os.Runtime.Services.GetRequiredService<FeatureCatalog>();
        var bundle = await catalog.Find(ShippedApps.Publisher, "customer-researcher");
        Assert.NotNull(bundle);

        var opNames = bundle.Manifest.Operations.Select(o => o.Name).ToArray();
        Assert.Contains("research", opNames);
        Assert.Contains("clear", opNames);
        Assert.Contains("refresh", opNames);

        var slotNames = bundle.Manifest.Slots.Select(s => s.Name).ToArray();
        Assert.Contains("customers", slotNames);
        Assert.Contains("search", slotNames);
    }
}
