namespace DigitalBrain.Framework.Tests.Integration.Enforcement;

public sealed class BrainScopeFacts
{
    [Fact]
    public void TheIdIsTheBrainNameWhoeverOwnsIt()
    {
        Assert.Equal("one", BrainScope.Create("alice", "one").Id);
        Assert.Equal(BrainScope.Create("alice", "one").Id, BrainScope.Create("bob", "one").Id);
        Assert.NotEqual(BrainScope.Create("alice", "one").Id, BrainScope.Create("alice", "two").Id);
    }

    [Fact]
    public void AScopeIsItsIdAndOwnerOnly()
    {
        var scope = BrainScope.Create("alice", "one");
        Assert.Equal(("one", "alice"), (scope.Id, scope.Owner));
    }

    [Fact]
    public void AnUnstampedCallCannotResolveABrain()
        => Assert.Throws<InvalidOperationException>(() => _ = BrainScope.CurrentId());

    [Fact]
    public void InvalidIdsAreRejected()
    {
        Assert.False(BrainScope.IsValidId(null));
        Assert.False(BrainScope.IsValidId("a/b"));
        Assert.False(BrainScope.IsValidId("a\\b"));
        Assert.False(BrainScope.IsValidId(new string('x', 201)));
        Assert.True(BrainScope.IsValidId("personal"));
    }
}
