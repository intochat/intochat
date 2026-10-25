using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Tests.Integration;

// The assembly is Authority; hosted state written under the Platform names must still be found after the rename.
public sealed class AuthorityRenameFacts
{
    [Fact]
    public void AuthorityRenameKeepsPersistedNames()
    {
        Assert.Equal("IntoChatOS:Platform:Storage", IntoChatOSNames.BrainStorageConfigurationKey);
        // The brain log's directory is <Home>/state (StateHome), no longer a Platform:Storage key.
        Assert.Equal("IntoChatOS:Home", IntoChatOSNames.HomeKey);
        Assert.Equal(StateHome.StateDirectoryName, Path.GetFileName(new IntoChat.OS.Authority.Storage.BrainStoreOptions().Directory));
        Assert.Equal("IntoChatOS:Platform:Fees", FeeOptions.Key);
        Assert.Equal("platform", IntoChatOSNames.PlatformGrainStorage);
        Assert.Equal("cluster", IntoChatOSNames.ClusterGrainStorage);
        Assert.Equal("grainstate", IntoChatOSNames.GrainState);
        Assert.Equal("os-state", IntoChatOSNames.GrainStateContainer);
    }
}
