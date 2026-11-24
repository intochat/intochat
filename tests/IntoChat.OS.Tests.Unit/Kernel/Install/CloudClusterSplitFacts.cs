using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

// The Cloud app is its own Orleans cluster with its own storage namespace: it never shares a cluster id, service id,
// grain-state container, lease container or Home with the OS app, so neither silo can join or activate the other's grains.
// The variables are found by the OS app's values, so the facts hold whatever the variables are called.
public sealed class CloudClusterSplitFacts
{
    private static InstallConfig WithCloud() => InstallConfig.Universal(
        env: "test",
        subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
        includeCloudApp: true,
        includeSandboxApp: false).Validated();

    private static (ContainerAppSpec Os, ContainerAppSpec Cloud) Apps(InstallConfig config, Dictionary<string, string>? extra = null)
    {
        var variables = extra ?? [];
        return (InstallPlan.OsApp(config, variables, []), InstallPlan.CloudApp(config, variables, [])!);
    }

    // The OS app's variables that carry this namespace value: the cluster and service ids, the grain-state container, the lease
    // container URI and Home.
    private static string[] KeysOf(ContainerAppSpec os, Func<string, bool> value)
        => [.. os.Variables.Where(pair => value(pair.Value)).Select(pair => pair.Key)];

    [Fact]
    public void TheOsAppKeepsItsNamespace()
    {
        var config = WithCloud();
        var (os, _) = Apps(config);

        Assert.NotEmpty(KeysOf(os, value => value == config.ClusterId));
        Assert.NotEmpty(KeysOf(os, value => value == config.ServiceId));
        Assert.Single(KeysOf(os, value => value == "grainstate-" + config.StorageFormat));
        Assert.Single(KeysOf(os, value => value == "/data/brain/" + config.StorageFormat));
        Assert.Single(KeysOf(os, value => value.EndsWith("/brainlease-" + config.StorageFormat, StringComparison.Ordinal)));
        Assert.Empty(os.InternalPorts);
        Assert.Equal(InstallPlan.BrainLogShare, Assert.Single(os.VolumeMounts).EnvironmentStorage);
    }

    [Fact]
    public void TheCloudAppHasItsOwnClusterServiceGrainStateLeaseAndHome()
    {
        var config = WithCloud();
        var (os, cloud) = Apps(config);

        foreach (var key in KeysOf(os, value => value == config.ClusterId || value == config.ServiceId))
        {
            Assert.Equal(os.Variables[key] + "-cloud", cloud.Variables[key]);
        }
        Assert.Equal("grainstate-cloud-" + config.StorageFormat, cloud.Variables[Assert.Single(KeysOf(os, value => value == "grainstate-" + config.StorageFormat))]);
        Assert.EndsWith("/brainlease-cloud-" + config.StorageFormat,
            cloud.Variables[Assert.Single(KeysOf(os, value => value.EndsWith("/brainlease-" + config.StorageFormat, StringComparison.Ordinal)))], StringComparison.Ordinal);
        var home = cloud.Variables[Assert.Single(KeysOf(os, value => value == "/data/brain/" + config.StorageFormat))];
        Assert.Equal("/data/brain-cloud/" + config.StorageFormat, home);

        var mount = Assert.Single(cloud.VolumeMounts);
        Assert.Equal(InstallPlan.CloudBrainLogShare, mount.EnvironmentStorage);
        Assert.Equal(home, mount.MountPath);
    }

    [Fact]
    public void TheCloudAppSharesNoNamespaceValueWithTheOsApp()
    {
        var config = WithCloud();
        var (os, cloud) = Apps(config);
        string[] namespaceValues = [config.ClusterId, config.ServiceId, "grainstate-" + config.StorageFormat, "/data/brain/" + config.StorageFormat];

        Assert.DoesNotContain(cloud.Variables.Values, value => namespaceValues.Contains(value, StringComparer.Ordinal)
            || value.EndsWith("/brainlease-" + config.StorageFormat, StringComparison.Ordinal));
        Assert.NotEqual(Assert.Single(os.VolumeMounts).EnvironmentStorage, Assert.Single(cloud.VolumeMounts).EnvironmentStorage);
    }

    [Fact]
    public void TheCloudAppPublishesNoGatewayPort() => Assert.Empty(Apps(WithCloud()).Cloud.InternalPorts);

    // No OS host is an Orleans cluster client, so no app of any install shape publishes the Orleans gateway port (30000) or any
    // other internal port beside its ingress.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NoAppPublishesAnOrleansGatewayPort(bool includeCloudApp, bool includeSandboxApp)
    {
        var config = InstallConfig.Universal(env: "test", subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: includeCloudApp, includeSandboxApp: includeSandboxApp).Validated();

        var apps = InstallPlan.Build(config).OfType<ContainerAppSpec>().ToArray();

        Assert.NotEmpty(apps);
        Assert.All(apps, app => Assert.Empty(app.InternalPorts));
    }

    [Fact]
    public void AModuleDeploymentVariableCannotPutTheCloudBackIntoTheOsNamespace()
    {
        var config = WithCloud();
        var (plainOs, _) = Apps(config);
        // A module deployment that wrote the OS namespace values under the same variables.
        var extra = KeysOf(plainOs, value => value == config.ClusterId || value == "/data/brain/" + config.StorageFormat)
            .ToDictionary(key => key, key => plainOs.Variables[key], StringComparer.Ordinal);

        var (_, cloud) = Apps(config, extra);

        Assert.All(extra.Keys, key => Assert.NotEqual(extra[key], cloud.Variables[key]));
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
