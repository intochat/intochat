using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

// The Cloud app is its own Orleans cluster with its own storage namespace: it never shares a cluster id, service id,
// grain-state container, lease container or Home with the OS app, so neither silo can join or activate the other's grains.
public sealed class CloudClusterSplitFacts
{
    private static readonly string Home = ConfigurationKeyGrammar.EnvironmentName(IntoChatOSNames.HomeKey);
    private static readonly string Lease = ConfigurationKeyGrammar.EnvironmentName(IntoChatOSNames.BrainStorageOwnershipContainerUriConfigurationKey);

    private static InstallConfig WithCloud() => InstallConfig.Universal(
        env: "test",
        subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
        includeCloudApp: true,
        includeSandboxApp: false);

    private static (ContainerAppSpec Os, ContainerAppSpec Cloud) Apps(InstallConfig config, Dictionary<string, string>? extra = null)
    {
        var variables = extra ?? [];
        return (InstallPlan.OsApp(config, variables, []), InstallPlan.CloudApp(config, variables, [])!);
    }

    private static string GrainState(ContainerAppSpec app)
        => Assert.Single(app.Variables, pair => pair.Key.EndsWith("__GrainStateContainer", StringComparison.Ordinal)).Value;

    [Fact]
    public void TheOsAppKeepsItsNamespace()
    {
        var config = WithCloud().Validated();
        var (os, _) = Apps(config);

        Assert.Equal(config.ClusterId, os.Variables["Orleans__ClusterId"]);
        Assert.Equal(config.ServiceId, os.Variables["Orleans__ServiceId"]);
        Assert.Equal("grainstate-" + config.StorageFormat, GrainState(os));
        Assert.Equal("/data/brain/" + config.StorageFormat, os.Variables[Home]);
        Assert.EndsWith("/brainlease-" + config.StorageFormat, os.Variables[Lease], StringComparison.Ordinal);
        Assert.Equal([InstallPlan.GatewayPort], os.InternalPorts);
        Assert.Equal(InstallPlan.BrainLogShare, Assert.Single(os.VolumeMounts).EnvironmentStorage);
    }

    [Fact]
    public void TheCloudAppSharesNoClusterOrStateWithTheOsApp()
    {
        var (os, cloud) = Apps(WithCloud());

        Assert.NotEqual(os.Variables["Orleans__ClusterId"], cloud.Variables["Orleans__ClusterId"]);
        Assert.NotEqual(os.Variables["Orleans__ServiceId"], cloud.Variables["Orleans__ServiceId"]);
        Assert.NotEqual(GrainState(os), GrainState(cloud));
        Assert.NotEqual(os.Variables[Home], cloud.Variables[Home]);
        Assert.NotEqual(os.Variables[Lease], cloud.Variables[Lease]);
        var osMount = Assert.Single(os.VolumeMounts);
        var cloudMount = Assert.Single(cloud.VolumeMounts);
        Assert.NotEqual(osMount.EnvironmentStorage, cloudMount.EnvironmentStorage);
        Assert.Equal(cloud.Variables[Home], cloudMount.MountPath);
    }

    [Fact]
    public void TheCloudNamespaceIsDerivedFromTheInstallNames()
    {
        var config = WithCloud().Validated();
        var (_, cloud) = Apps(config);

        Assert.Equal(config.ClusterId + "-cloud", cloud.Variables["Orleans__ClusterId"]);
        Assert.Equal(config.ServiceId + "-cloud", cloud.Variables["Orleans__ServiceId"]);
        Assert.Equal("grainstate-cloud-" + config.StorageFormat, GrainState(cloud));
        Assert.EndsWith("/brainlease-cloud-" + config.StorageFormat, cloud.Variables[Lease], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCloudAppPublishesNoGatewayPort() => Assert.Empty(Apps(WithCloud()).Cloud.InternalPorts);

    [Fact]
    public void AModuleDeploymentVariableCannotPutTheCloudBackIntoTheOsCluster()
    {
        var config = WithCloud().Validated();
        var (_, cloud) = Apps(config, new Dictionary<string, string> { ["Orleans__ClusterId"] = config.ClusterId, [Home] = "/data/brain/" + config.StorageFormat });

        Assert.Equal(config.ClusterId + "-cloud", cloud.Variables["Orleans__ClusterId"]);
        Assert.NotEqual("/data/brain/" + config.StorageFormat, cloud.Variables[Home]);
    }

    [Fact]
    public void TheFoundationHoldsTheCloudStorageOnlyWithTheCloudApp()
    {
        var withCloud = InstallPlan.Foundation(WithCloud());
        var without = InstallPlan.Foundation(InstallConfig.Universal(env: "test", subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2", includeCloudApp: false, includeSandboxApp: false));

        Assert.Contains(withCloud, spec => spec is FileShareSpec { Name: InstallPlan.CloudBrainLogShare });
        Assert.Contains(withCloud, spec => spec is BlobContainerSpec { Name: var name } && name.StartsWith("grainstate-cloud-", StringComparison.Ordinal));
        Assert.Contains(withCloud, spec => spec is BlobContainerSpec { Name: var name } && name.StartsWith("brainlease-cloud-", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(withCloud.OfType<ManagedEnvironmentSpec>()).Storages, storage => storage.Name == InstallPlan.CloudBrainLogShare);

        Assert.DoesNotContain(without, spec => spec is FileShareSpec { Name: InstallPlan.CloudBrainLogShare });
        Assert.DoesNotContain(without, spec => spec is BlobContainerSpec { Name: var name } && name.Contains("-cloud-", StringComparison.Ordinal));
        Assert.Single(Assert.Single(without.OfType<ManagedEnvironmentSpec>()).Storages);
    }
}
