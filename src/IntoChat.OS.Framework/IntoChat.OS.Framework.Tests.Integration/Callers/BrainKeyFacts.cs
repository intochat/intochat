using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration.Callers;

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

    [Theory]
    [InlineData("brain/ui/volume", "brain")]
    [InlineData("brain/apps/notes", "brain")]
    [InlineData("brain:volume", null)]
    [InlineData("brain", null)]
    [InlineData("@platform/marketplace", null)]
    public void OnlyASlashSeparatesABrainFromTheRestOfAKey(string key, string? scope)
        => Assert.Equal(scope, BrainKey.ScopeOf(key));

    [Theory]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("@platform")]
    public void ABrainIdHasNoSeparatorAndNoReservedPrefix(string id)
        => Assert.False(BrainScope.IsValidId(id));
}
