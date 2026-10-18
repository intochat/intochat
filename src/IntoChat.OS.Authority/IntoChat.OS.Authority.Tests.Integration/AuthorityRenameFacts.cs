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
        Assert.Equal("IntoChatOS:Platform:Storage:Directory", IntoChatOSNames.BrainStorageDirectoryConfigurationKey);
        Assert.Equal("IntoChatOS:Platform:Fees", FeeOptions.Key);
        Assert.Equal("platform", IntoChatOSNames.PlatformGrainStorage);
        Assert.Equal("cluster", IntoChatOSNames.ClusterGrainStorage);
        Assert.Equal("grainstate", IntoChatOSNames.GrainState);
        Assert.Equal("intochat-os-state", IntoChatOSNames.GrainStateContainer);
    }
}
