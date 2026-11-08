using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class DevReloadFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dev-apps-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TopologyOtherThanLocalHidesTheRoute()
    {
        Assert.True(DevAppAccess.IsLocal(new ConfigurationBuilder().Build()));
        Assert.True(DevAppAccess.IsLocal(new ConfigurationBuilder().AddInMemoryCollection([new("IntoChatOS:Topology", "local")]).Build()));
        Assert.False(DevAppAccess.IsLocal(new ConfigurationBuilder().AddInMemoryCollection([new("IntoChatOS:Topology", "cloud")]).Build()));
    }

    private static AppBundle Bundle(string version, string source)
        => FeatureFolder.ToBundle("notes", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppFiles.DeclarationPath] = Declarations.Of("notes", version, "Notes", "Notes."),
            ["note.cs"] = source,
        });

}
