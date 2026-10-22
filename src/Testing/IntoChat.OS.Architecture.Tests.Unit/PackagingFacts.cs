using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// VERSION is the single source of package versions; pipelines add only a pre.N suffix, so no
// VersionSuffix may remain in the props. The OS host packs, but the linked features folder never rides along.
public sealed partial class PackagingFacts
{
    // The SDK tier (decision 15): the vocabulary, the clients and the testing harness, plus every *.Contracts package.
    private static readonly string[] SdkPackages =
    [
        "IntoChat.OS", "IntoChat.OS.Client", "IntoChat.OS.Client.Orleans", "IntoChat.OS.Aspire.Client",
        "IntoChat.OS.Testing", "IntoChat.OS.Testing.Aspire", "IntoChat.OS.Aspire.Testing", "IntoChat.OS.Testing.Playwright",
        "IntoChat.OS.Sdk", "IntoChat.OS.Sdk.Identity", "IntoChat.OS.Sdk.Http", "IntoChat.OS.Contracts.Compute",
    ];

    [GeneratedRegex(@"'\$\(MSBuildProjectName\)' == '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSdkPackage();

    // MIT is stamped from Directory.Build.props only, and only on the SDK tier; runtime packages carry no licence
    // expression until the runtime licence is decided (handbook vision/commercial.md).
    [Fact]
    public void OnlySdkPackagesCarryTheMitStamp()
    {
        var props = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("""<PackageLicenseExpression Condition="'$(IntoChatOSSdkPackage)' == 'true'">MIT</PackageLicenseExpression>""", props, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(props, "<PackageLicenseExpression", RegexOptions.CultureInvariant));
        Assert.Contains("$(MSBuildProjectName.EndsWith('.Contracts'))", props, StringComparison.Ordinal);
        Assert.Equal(SdkPackages.Order(StringComparer.Ordinal), NamedSdkPackage().Matches(props).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        var packable = Repository.Projects.Values.Where(project => File.ReadAllText(project.Path).Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal)).ToArray();
        Assert.All(SdkPackages, name => Assert.Contains(packable, project => project.Name == name));
        Assert.All(packable, project =>
        {
            var csproj = File.ReadAllText(project.Path);
            Assert.DoesNotContain("PackageLicenseExpression", csproj, StringComparison.Ordinal);
            Assert.DoesNotContain("PackageLicenseFile", csproj, StringComparison.Ordinal);
        });
        Assert.Contains(packable, project => project.Name == "IntoChat.OS.Authority.Hosted");
    }

    [Fact]
    public void VersionPrefixEqualsVersionFile()
    {
        var version = File.ReadAllText(Path.Combine(Repository.Root, "VERSION")).Trim();
        var props = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("<VersionPrefix>$(IntoChatOSVersion)</VersionPrefix>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<VersionSuffix>", props, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), version);
    }

    [Fact]
    public void OsHostIsPackableWithoutFeatures()
    {
        var csproj = File.ReadAllText(Repository.ProjectNamed("IntoChat.OS.Kernel").Path);
        Assert.Contains("<IsPackable>true</IsPackable>", csproj, StringComparison.Ordinal);
        Assert.Contains("Pack=\"false\"", csproj, StringComparison.Ordinal);
    }
}
