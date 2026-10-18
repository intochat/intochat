using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests.Unit;

// VERSION is the single source of package versions; pipelines add only a pre.N suffix, so no
// VersionSuffix may remain in the props. The OS host packs, but the linked features folder never rides along.
public sealed class PackagingFacts
{
    [Fact]
    public void VersionPrefixEqualsVersionFile()
    {
        var version = File.ReadAllText(Path.Combine(Repository.Root, "VERSION")).Trim();
        var props = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("<VersionPrefix>$(DigitalBrainVersion)</VersionPrefix>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<VersionSuffix>", props, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), version);
    }

    [Fact]
    public void OsHostIsPackableWithoutFeatures()
    {
        var csproj = File.ReadAllText(Repository.ProjectNamed("DigitalBrain.OS").Path);
        Assert.Contains("<IsPackable>true</IsPackable>", csproj, StringComparison.Ordinal);
        Assert.Contains("Pack=\"false\"", csproj, StringComparison.Ordinal);
    }
}
