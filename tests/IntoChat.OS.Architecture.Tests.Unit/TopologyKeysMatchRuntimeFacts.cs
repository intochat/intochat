using DigitalBrain.Runtime.Orleans;
using OsNames = IntoChat.OS.Host.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The os configuration names stay dependency-free for deployments, so they hold the runtime's strings rather than referencing
// them (03-12, R4-09): this fact keeps the two in step.
public sealed class TopologyKeysMatchRuntimeFacts
{
    [Fact]
    public void TheOsTopologyNamesAreTheRuntimeNames()
    {
        var runtime = IntoChat.OS.Kernel.Composition.Kernel.OsRuntimeOptions.Value;
        Assert.Equal(OsNames.TopologyKey, runtime.TopologyKey);
        Assert.Equal(OsNames.HomeKey, runtime.HomeKey);
        Assert.Equal(OsNames.BindKey, runtime.BindKey);
        Assert.Equal(OsNames.CloudStorageConfigurationKey, runtime.CloudStorageKey);
        Assert.Equal(OsNames.CloudKeyVaultConfigurationKey, runtime.CloudKeyVaultKey);
        Assert.Equal(OsNames.MasterKeyConfigurationKey, runtime.MasterKeyKey);
        Assert.Equal(OsNames.PlatformGrainStorage, runtime.PlatformGrainStorage);
        Assert.Equal(OsNames.ClusterGrainStorage, runtime.ClusterGrainStorage);
        Assert.Equal(OsNames.GrainStateContainer, runtime.GrainStateContainer);
    }

    [Fact]
    public void TheOsTopologyValuesAreTheRuntimeValues()
    {
        Assert.Equal(OsNames.TopologyLocal, Topology.Local.ToConfigValue());
        Assert.Equal(OsNames.TopologyLocalCloud, Topology.LocalCloud.ToConfigValue());
        Assert.Equal(OsNames.TopologyCloud, Topology.Cloud.ToConfigValue());
    }
}
