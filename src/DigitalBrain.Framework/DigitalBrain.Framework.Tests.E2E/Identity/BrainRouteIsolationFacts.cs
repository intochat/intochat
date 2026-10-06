using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Slider;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Framework.Tests.E2E.Identity;

public sealed class BrainRouteIsolationFacts(ReferenceBrainFixture host)
{

    [Fact(Timeout = 180_000)]
    public async Task ASignedInPrincipalCannotReachAnotherPrincipalsWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var alicePerson = await People.SignedIn(brain.HttpClient, "alice-" + suffix, ct);
        using var bobPerson = await People.SignedIn(brain.HttpClient, "bob-" + suffix, ct);
        var alice = alicePerson.Client;
        var bob = bobPerson.Client;
        var aliceBrain = alicePerson.Workspace;
        var bobBrain = bobPerson.Workspace;
        // Bob reaches his own workspace but is forbidden from Alice's scoped value and app node.
        using var ownUi = await bob.GetAsync($"/brains/{bobBrain}/ui/sliders/volume", ct);
        Assert.Equal(HttpStatusCode.OK, ownUi.StatusCode);

        using var foreignWorkspace = await bob.GetAsync($"/brains/{aliceBrain}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignWorkspace.StatusCode);
        using var foreignSources = await bob.PostAsJsonAsync($"/brains/{aliceBrain}/connected-sources", new { sources = new[] { "stolen" } }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignSources.StatusCode);

        using var foreignUi = await bob.GetAsync($"/brains/{aliceBrain}/ui/sliders/volume", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignUi.StatusCode);

        using var foreignNode = await bob.GetAsync($"/brains/{aliceBrain}/apps/node?kind=text&name={aliceBrain}/apps/forms/intake", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignNode.StatusCode);

        using var foreignCompute = await bob.GetAsync($"/brains/{aliceBrain}/compute/usage", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignCompute.StatusCode);
        using var ownCompute = await bob.GetAsync($"/brains/{bobBrain}/compute/usage", ct);
        Assert.Equal(HttpStatusCode.OK, ownCompute.StatusCode);

        var aliceLedger = brain.Get<DigitalBrain.Compute.IAllowanceLedger>(alicePerson.Account);
        await aliceLedger.GrantAsync(new DigitalBrain.Compute.Allowance
        {
            AllowanceId = "budget",
            AccountId = alicePerson.Account,
            BrainId = aliceBrain,
            Level = DigitalBrain.Compute.ApprovalLevel.StandingBudget,
            Scope = DigitalBrain.Compute.AllowanceScope.Always,
            LimitCompute = 100m,
            PriceBookVersion = new DigitalBrain.Compute.PriceBook().Version,
            GrantedAt = DateTimeOffset.UtcNow,
        }, ct);
        var reserved = await aliceLedger.AuthorizeAsync(new DigitalBrain.Compute.AllowanceRequest(alicePerson.Account, aliceBrain, null, "private-charge", DigitalBrain.Compute.AllowanceCallerKind.Assistant,
            "neuron", "chat", [], 7m, true), ct);
        Assert.True(reserved.Allowed, reserved.Explanation);
        await aliceLedger.SettleAsync(reserved.ReservationId!, 7m, DigitalBrain.Compute.FailureClass.None, ct);
        using var aliceSummary = JsonDocument.Parse(await alice.GetStringAsync("/compute/summary", ct));
        using var bobSummary = JsonDocument.Parse(await bob.GetStringAsync("/compute/summary", ct));
        Assert.Equal(7m, aliceSummary.RootElement.GetProperty("chargedCompute").GetDecimal());
        Assert.Equal(0m, bobSummary.RootElement.GetProperty("chargedCompute").GetDecimal());
    }

}
