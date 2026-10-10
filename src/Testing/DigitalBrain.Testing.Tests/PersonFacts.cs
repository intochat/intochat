using System.Net;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Neurons;

namespace DigitalBrain.Testing.Tests;

public sealed class PersonFacts
{
    [Fact]
    public async Task TheOpenPostureOwnerIsAvailableWithoutRegistering()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        Assert.Equal("owner", brain.Owner.PrincipalId);
        Assert.Equal("owner", brain.Owner.BrainId);
        using (brain.Owner.AsCaller()) { Assert.Equal("owner", BrainScope.CurrentId()); }
    }

    [Fact]
    public async Task ARegisteredPersonOwnsABrainAndCallsAsItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        var alice = await brain.PersonAsync("alice", ct);
        using var session = await alice.Http.GetAsync("/identity/session", ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using (alice.AsCaller())
        {
            Assert.Equal(alice.BrainId, BrainScope.CurrentId());
            var self = await brain.Get<IBrain>(alice.BrainId).Read();
            Assert.Equal(alice.AccountId, self.Owner.AccountId);
        }
        Assert.Null(Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey));
    }
}
