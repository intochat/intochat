using IntoChat.Cloud;
using IntoChat.OS.Kernel;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class CloudCompositionFacts
{
    private static readonly string[] CloudModuleIds =
    [
        "auth",
        "blob",
        "circle-server",
        "hosting",
        "lobby",
        "localization",
        "mail",
        "organisations",
    ];

    [Fact]
    public void KernelModulesExcludeAllCloudModules()
    {
        var ids = OperatingSystemModules.Modules
            .Select(module => module.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in CloudModuleIds)
        {
            Assert.DoesNotContain(id, ids);
        }
    }

    [Fact]
    public void CloudHostDiscoveryIncludesAllCloudAndKernelModules()
    {
        var cloudIds = OperatingSystemModules.Discover(typeof(CloudHost).Assembly)
            .Select(module => module.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in CloudModuleIds)
        {
            Assert.Contains(id, cloudIds);
        }

        foreach (var id in OperatingSystemModules.Modules.Select(module => module.Id.Value))
        {
            Assert.Contains(id, cloudIds);
        }
    }
}
