using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Framework.Tests.Unit.Parts;

public sealed class PartIdFacts
{
    [Theory]
    [InlineData("home", "home/core")]
    [InlineData("home/kernel", "home/core")]
    [InlineData("home/connections/gmail/1", "home/core")]
    [InlineData("home/apps", "home/core")]
    [InlineData("home/apps/filmcraft", "home/apps/filmcraft")]
    [InlineData("home/apps/filmcraft/tables/clips", "home/apps/filmcraft")]
    public void AKeyBelongsToItsAppOrToItsBrainsCore(string key, string part) => Assert.Equal(part, PartId.Of(key).Value);

    [Theory]
    [InlineData("")]
    [InlineData("@platform/ledger")]
    [InlineData("bad:brain/x")]
    public void AKeyOutsideEveryBrainIsRefused(string key) => Assert.Throws<ArgumentException>(() => PartId.Of(key));
}
