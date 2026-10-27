using DigitalBrain.Runtime.Orleans;
using OsNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;
using RuntimeNames = DigitalBrain.Runtime.Orleans.IntoChatOSNames;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The os configuration names stay dependency-free for deployments, so they hold the runtime's strings rather than referencing
// them (03-12, R4-09): this fact keeps the two in step.
public sealed class TopologyKeysMatchRuntimeFacts
{
    [Fact]
    public void TheOsTopologyNamesAreTheRuntimeNames()
    {
        Assert.Equal(RuntimeNames.TopologyKey, OsNames.TopologyKey);
        Assert.Equal(RuntimeNames.HomeKey, OsNames.HomeKey);
        Assert.Equal(RuntimeNames.BindKey, OsNames.BindKey);
        Assert.Equal(RuntimeNames.CloudStorageKey, OsNames.CloudStorageConfigurationKey);
        Assert.Equal(RuntimeNames.CloudKeyVaultKey, OsNames.CloudKeyVaultConfigurationKey);
        Assert.Equal(RuntimeNames.MasterKeyKey, OsNames.MasterKeyConfigurationKey);
        Assert.Equal(RuntimeNames.PlatformGrainStorage, OsNames.PlatformGrainStorage);
        Assert.Equal(RuntimeNames.ClusterGrainStorage, OsNames.ClusterGrainStorage);
        Assert.Equal(RuntimeNames.GrainStateContainer, OsNames.GrainStateContainer);
    }

    [Fact]
    public void TheOsTopologyValuesAreTheRuntimeValues()
    {
        Assert.Equal(OsNames.TopologyLocal, Topology.Local.ToConfigValue());
        Assert.Equal(OsNames.TopologyLocalCloud, Topology.LocalCloud.ToConfigValue());
        Assert.Equal(OsNames.TopologyCloud, Topology.Cloud.ToConfigValue());
    }
}
