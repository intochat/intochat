using System.Net;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Testing.Neurons;

namespace IntoChat.OS.Testing.Tests.Integration;

public sealed class PersonFacts(TestingHost host)
{
    [Fact]
    public async Task TheOpenPostureOwnerIsAvailableWithoutRegistering()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        Assert.Equal("owner", brain.Owner.PrincipalId);
        Assert.Equal("owner", brain.Owner.BrainId);
        using (brain.Owner.AsCaller()) { Assert.Equal("owner", BrainScope.CurrentId()); }
    }

    [Fact]
    public async Task ARegisteredPersonOwnsABrainAndCallsAsItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
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

    [Fact]
    public async Task EachPersonsScopeRunsUnderItsOwnTraceAndLeavesNoneBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);
        using (alice.AsCaller()) { Assert.Equal(alice.AccountId, TraceContext.Current?.PayerAccountId); }
        Assert.Null(TraceContext.Current);
        using (bob.AsCaller())
        {
            Assert.Equal(bob.AccountId, TraceContext.Current?.PayerAccountId);
            Assert.Equal(bob.BrainId, TraceContext.Current?.BrainId);
        }
        Assert.Null(TraceContext.Current);
    }
}
