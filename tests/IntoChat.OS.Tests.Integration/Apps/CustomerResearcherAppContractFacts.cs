using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class CustomerResearcherAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<CustomerResearcherApp, FlagshipAppsRuntimeFixture>(fixture)
{
    protected override string? ConcurrentActivationKey => "btn:clear";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ResearchAndClear_PersistsProfilesAndClearsCleanly_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("Customer Researcher");
        var initialGen = app.CurrentView.Generation;

        await app.Send(new ResearchCompany("Acme Corp", "SaaS", "Enterprise workflow automation", 92), ct);
        Assert.True(app.CurrentView.Generation > initialGen, "Expected generation to increment after researching Acme Corp.");
        app.ShouldDisplayText("Acme Corp")
            .ShouldDisplayText("SaaS")
            .ShouldDisplayText("92");

        var afterFirstGen = app.CurrentView.Generation;
        await app.Send(new ResearchCompany("Globex", "AI", "Autonomous agents", 88), ct);
        Assert.True(app.CurrentView.Generation > afterFirstGen, "Expected generation to increment after researching Globex.");
        app.ShouldDisplayText("Acme Corp")
            .ShouldDisplayText("Globex")
            .ShouldDisplayText("AI")
            .ShouldDisplayText("88");

        await app.Click("btn:clear", ct);
        app.ShouldDisplayText("No companies researched yet");
    }
}
