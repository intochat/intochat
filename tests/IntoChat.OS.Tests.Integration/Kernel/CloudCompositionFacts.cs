using IntoChat.OS.Kernel;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class CloudCompositionFacts
{
    [Fact]
    public void CloudModulesLoadUnderCloudAndNeverUnderLocal()
    {
        var ids = OperatingSystemModules.Modules
            .Where(module => module.LoadsUnder("cloud") && !module.LoadsUnder("local"))
            .Select(module => module.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["auth", "blob", "circle-server", "hosting", "lobby", "localization", "mail", "organisations"], ids);
    }

    [Fact]
    public void CloudOnlyModulesNeverLoadUnderLocalOrLocalCloud()
    {
        string[] ids = ["auth", "circle-server", "hosting", "lobby", "localization", "mail", "organisations"];
        var modules = OperatingSystemModules.Modules;
        foreach (var id in ids)
        {
            var module = Assert.Single(modules, candidate => string.Equals(candidate.Id.Value, id, StringComparison.Ordinal));
            Assert.True(module.LoadsUnder("cloud"));
            Assert.False(module.LoadsUnder("local"));
            Assert.False(module.LoadsUnder("local+cloud"));
        }
    }
}
