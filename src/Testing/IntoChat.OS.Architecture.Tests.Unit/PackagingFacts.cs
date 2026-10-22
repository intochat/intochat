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

    // The templatepack lives outside the solution (templates/), so it is named in the MIT condition but is not a solution project.
    private const string TemplatePack = "IntoChat.OS.Templates";

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
        Assert.Equal(SdkPackages.Append(TemplatePack).Order(StringComparer.Ordinal), NamedSdkPackage().Matches(props).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
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

    // A module that hosts its own contracts (IntoChatOSContractsHost) ships them inside its runtime package, so
    // that package is not MIT-stamped and is not an SDK package; the declaration is the only thing that marks it.
    [Fact]
    public void AModuleThatHostsItsContractsIsARuntimePackage()
    {
        var hosts = Repository.Projects.Values.Where(project => File.ReadAllText(project.Path).Contains("<IntoChatOSContractsHost>true</IntoChatOSContractsHost>", StringComparison.Ordinal)).ToArray();
        Assert.All(hosts, project =>
        {
            Assert.False(project.Name.EndsWith(".Contracts", StringComparison.Ordinal), project.Name);
            Assert.DoesNotContain(project.Name, SdkPackages);
            Assert.Contains(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name == project.Name && ContractRing.IsHost(assembly));
        });
        Assert.Equal(hosts.Select(project => project.Name).Order(StringComparer.Ordinal),
            ProductionArchitecture.Assemblies.Where(ContractRing.IsHost).Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTemplatePackLivesOutsideTheSolutionAndIsMit()
    {
        var csproj = File.ReadAllText(Path.Combine(Repository.Root, "templates", TemplatePack, TemplatePack + ".csproj"));
        Assert.Contains("<PackageType>Template</PackageType>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageLicenseExpression", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain(Repository.Projects.Keys, name => name == TemplatePack);
        Assert.DoesNotContain("templates/", File.ReadAllText(Path.Combine(Repository.Root, "Intochat.OS.slnx")), StringComparison.Ordinal);
        Assert.Contains($"'$(MSBuildProjectName)' == '{TemplatePack}'", File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props")), StringComparison.Ordinal);
        var content = Path.Combine(Repository.Root, "templates", TemplatePack, "content");
        Assert.DoesNotContain(Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories), path => File.ReadAllText(path).Contains("IntoChatOSModule", StringComparison.Ordinal));
        Assert.DoesNotContain("<PropertyGroup", File.ReadAllText(Path.Combine(content, "intochat-module", "Directory.Build.props")), StringComparison.Ordinal);
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
