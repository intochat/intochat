namespace DigitalBrain.Framework.Tests.Unit.Callers;

public sealed class BrainKeyFacts
{
    [Theory]
    [InlineData("brain", "brain")]
    [InlineData("brain/apps/notes", "brain")]
    [InlineData("brain/connection/postgres/c1", "brain")]
    public void TheBrainIsTheFirstSegmentOfAKey(string key, string brain)
        => Assert.Equal(brain, BrainKey.BrainOf(key));

    [Theory]
    [InlineData("brain", true)]
    [InlineData("brain/apps/notes", true)]
    [InlineData("brainy/apps/notes", false)]
    [InlineData("other/brain", false)]
    [InlineData("bra", false)]
    public void AKeyBelongsToABrainOnlyAtASegmentBoundary(string key, bool belongs)
        => Assert.Equal(belongs, BrainKey.BelongsTo(key, "brain"));
}
