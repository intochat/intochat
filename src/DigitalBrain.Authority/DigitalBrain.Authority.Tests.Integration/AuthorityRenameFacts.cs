using DigitalBrain.Authority.Compute;
using DigitalBrain.Framework;

namespace DigitalBrain.Authority.Tests.Integration;

// The assembly is Authority; hosted state written under the Platform names must still be found after the rename.
public sealed class AuthorityRenameFacts
{
    [Fact]
    public void AuthorityRenameKeepsPersistedNames()
    {
        Assert.Equal("DigitalBrain:Platform:Storage", DigitalBrainNames.BrainStorageConfigurationKey);
        Assert.Equal("DigitalBrain:Platform:Storage:Directory", DigitalBrainNames.BrainStorageDirectoryConfigurationKey);
        Assert.Equal("DigitalBrain:Platform:Fees", FeeOptions.Key);
        Assert.Equal("platform", DigitalBrainNames.PlatformGrainStorage);
        Assert.Equal("cluster", DigitalBrainNames.ClusterGrainStorage);
        Assert.Equal("grainstate", DigitalBrainNames.GrainState);
        Assert.Equal("digitalbrain-v2-state", DigitalBrainNames.GrainStateContainer);
    }
}
