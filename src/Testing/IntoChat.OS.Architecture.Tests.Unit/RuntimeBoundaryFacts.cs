using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Orleans package pins stay referenced. The two *OnlyShrink ratchets (grain-factory use and Orleans package
// references) are deleted in step 2; step 6 deletes every remaining *OnlyShrink fact.
public sealed partial class RuntimeBoundaryFacts
{
    // Every Orleans package pinned centrally is referenced by some project; an unused pin blurs the picture.
    // The DigitalBrain runtime packages are the fixed D-02 pin set (Directory.Packages.props) and are exempt.
    [Fact]
    public void EveryCentralOrleansPinIsReferenced()
    {
        var pinned = System.Xml.Linq.XDocument.Load(Path.Combine(Repository.Root, "Directory.Packages.props"))
            .Descendants("PackageVersion").Select(version => version.Attribute("Include")?.Value).OfType<string>()
            .Where(IsOrleansPackage).Where(package => !package.StartsWith("DigitalBrain.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var referenced = Repository.Projects.Values.SelectMany(project => Repository.PackageReferences(project.Path)).ToHashSet(StringComparer.Ordinal);

        var unused = pinned.Except(referenced).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unused.Length == 0, "Directory.Packages.props pins Orleans packages no project references:\n" + string.Join('\n', unused));
    }

    private static bool IsOrleansPackage(string package)
        => package.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal) || package.Contains(".Orleans", StringComparison.Ordinal);
}
