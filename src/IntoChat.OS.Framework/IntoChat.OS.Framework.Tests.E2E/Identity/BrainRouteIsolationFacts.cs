using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.E2E.Identity;

[Collection("ReferenceBrainFixture")]
public sealed class BrainRouteIsolationFacts(ReferenceBrainFixture host)
{
    [Fact(Timeout = 180_000)]
    public async Task ASignedInPrincipalCannotReachAnotherPrincipalsWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var alicePerson = await brain.PersonAsync("alice-" + suffix, ct);
        using var bobPerson = await brain.PersonAsync("bob-" + suffix, ct);
        var alice = alicePerson.Http;
        var bob = bobPerson.Http;
        var aliceBrain = alicePerson.BrainId;
        var bobBrain = bobPerson.BrainId;
        // Bob reaches his own workspace but is forbidden from Alice's scoped value and app node.
        using var ownUi = await bob.GetAsync($"/brains/{bobBrain}/apps/node?kind=text&name={bobBrain}/apps/ino/status", ct);
        Assert.Equal(HttpStatusCode.OK, ownUi.StatusCode);

        using var foreignWorkspace = await bob.GetAsync($"/brains/{aliceBrain}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignWorkspace.StatusCode);
        using var foreignSources = await bob.PostAsJsonAsync($"/brains/{aliceBrain}/apps/workspace/request", new { operation = "open", input = "" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignSources.StatusCode);

        using var foreignUi = await bob.GetAsync($"/brains/{aliceBrain}/apps/node?kind=text&name={aliceBrain}/apps/ino/status", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignUi.StatusCode);

        using var foreignNode = await bob.GetAsync($"/brains/{aliceBrain}/apps/node?kind=text&name={aliceBrain}/apps/forms/intake", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignNode.StatusCode);

        using var foreignCompute = await bob.GetAsync($"/brains/{aliceBrain}/compute", ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignCompute.StatusCode);
        using var ownCompute = await bob.GetAsync($"/brains/{bobBrain}/compute", ct);
        Assert.Equal(HttpStatusCode.OK, ownCompute.StatusCode);
    }
}
