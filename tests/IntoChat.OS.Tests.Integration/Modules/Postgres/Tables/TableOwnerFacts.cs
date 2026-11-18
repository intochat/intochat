using DigitalBrain.Access;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using IntoChat.OS.Modules.Postgres.Tables;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration.Tables;

public sealed class TableOwnerFacts
{
    [Fact]
    public void AnOwnerRoundTripsThroughItsGrainKey()
    {
        var owner = new TableOwner("brain-1", "brain-1/apps/notes");
        Assert.Equal(owner, TableOwner.Parse(owner.Key));
        Assert.Equal("brain-1/tables/brain-1%2Fapps%2Fnotes", owner.Key);
    }

    [Theory]
    [InlineData("brain")]
    [InlineData("brain/tables")]
    [InlineData("brain/tables/app/extra")]
    [InlineData("/tables/app")]
    [InlineData("brain/tables/")]
    [InlineData("brain/apps/app")]
    [InlineData("brain/tables/a%2fb")]
    public void AMalformedKeyIsRefusedAsUnauthorized(string key)
        => Assert.Throws<UnauthorizedAccessException>(() => TableOwner.Parse(key));

    [Fact]
    public void TheCurrentOwnerComesFromTheStampedCallerAndRequiresAnApp()
    {
        var alice = new Actor.Person("alice", "alice");
        var caller = IntoChat.OS.Tests.TestEdge.Authority.App("brain-1/apps/notes", alice, new BrainId("brain-1"));
        // Grain keys carry the brain scope id, never the caller's raw brain name.
        Assert.Equal(new TableOwner(BrainScope.Create("alice", "brain-1").Id, "brain-1/apps/notes"), TableOwner.Current(caller));
        Assert.Throws<UnauthorizedAccessException>(() => TableOwner.Current(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("brain-1"))));
        Assert.Throws<UnauthorizedAccessException>(() => TableOwner.Current(null));
    }
}
