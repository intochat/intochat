using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Tests.Support.Fakes;

namespace IntoChat.OS.Tests.Integration.Journeys;

public sealed class ClickableSocialAndSharingUiJourney
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AliceAndBobShareCustomerResearcherTableUsingClickableUiButtons(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(
            kind,
            f => f.Postgres()
                  .WebSearch(w => w.Answer("Acme", new WebResult("Acme", new("https://acme.test"), "Acme moves freight."))),
            ct);

        var alice = os.Person("alice");
        var bob = os.Person("bob");
        await alice.Befriend(bob, ct);

        await using var aliceApp = await alice.Open<CustomerResearcherApp>(ct: ct);
        await aliceApp.Type("input:company", "Acme", ct);
        await aliceApp.Click("btn:research", ct);
        aliceApp.ShouldDisplayText("Acme");

        await aliceApp.Type("input:share-friend", bob.Account, ct);
        await aliceApp.Click("btn:share-viewer", ct);
        aliceApp.ShouldDisplayText(bob.Account);

        await using var bobApp = await bob.Open<CustomerResearcherApp>(ct: ct);
        bobApp.ShouldDisplayText("No companies researched yet");

        await bobApp.Type("input:connect-owner", alice.Brain.Value, ct);
        await bobApp.Click("btn:connect-shared", ct);

        bobApp.ShouldDisplayText("Acme");
        bobApp.ShouldNotHaveEnabled("btn:research");

        await bobApp.Click("btn:use-mine", ct);
        bobApp.ShouldDisplayText("No companies researched yet");
        bobApp.ShouldNotDisplayText("Acme");
    }
}
