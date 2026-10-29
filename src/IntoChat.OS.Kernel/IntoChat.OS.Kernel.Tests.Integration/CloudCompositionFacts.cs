using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Kernel;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class CloudCompositionFacts
{
    [Fact]
    public void CloudListsTheFiveModules()
    {
        var ids = CloudModules.For(Topology.Cloud).Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["AuthModule", "HostingModule", "LobbyModule", "MailModule", "OrganisationsModule"], ids);
    }

    [Fact]
    public void LocalAndLocalCloudListNone()
    {
        Assert.Empty(CloudModules.For(Topology.Local));
        Assert.Empty(CloudModules.For(Topology.LocalCloud));
    }

    [Fact]
    public void TheDiscoveredCatalogLoadsThoseIdsOnlyUnderCloud()
    {
        string[] ids = ["auth", "mail", "organisations", "lobby", "hosting"];
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
