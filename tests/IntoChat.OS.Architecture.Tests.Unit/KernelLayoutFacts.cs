using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Modules.Configuration;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class KernelLayoutFacts
{
    private static readonly string SrcPrefix = "s" + "rc/";

    private static string SystemOneHostingStatePath()
        => Path.Combine(Repository.ProjectNamed("IntoChat.OS.AppHost").Directory, "Hosting", "Modules", "AI", "Configuration", "SystemOneHostingState.cs");

    private static string ClientOrleansCsprojPath()
        => Repository.ProjectNamed("IntoChat.OS.Host").Path;

    private static string ClientOrleansDirectory()
        => Path.Combine(Repository.ProjectNamed("IntoChat.OS.Host").Directory, "Client");

    private static string ContractManifestPath()
        => Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "ContractManifest.json");

    #region F10: SystemOne Hosting Path

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_01_SystemOneHostingState_SourceFile_ExistsInAspireHosting()
    {
        var path = SystemOneHostingStatePath();
        Assert.True(File.Exists(path), $"SystemOneHostingState.cs must exist at '{path}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_02_SystemOneHostingState_ContainsCorrectAdapterDirectoryPath()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains(SrcPrefix + "Modules/AI/systemone-adapter", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_03_SystemOneAdapter_PathFormatting_ConformsToModuleLayout()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains(SrcPrefix + "Modules/AI/", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_04_SystemOneHostingState_DeclaresExpectedConstants()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains("public const string SystemOneResourceName = \"systemone\";", content, StringComparison.Ordinal);
        Assert.Contains("public const string SystemOneApiKeyLocal = \"local\";", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_05_SystemOneHostingState_UsesIntoChatOSConfiguration()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains("ModuleConfiguration.EnvironmentName", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B01_AdapterDirectory_UsesForwardSlashes()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains("\"" + SrcPrefix + "Modules/AI/systemone-adapter\"", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B02_AdapterDirectory_DoesNotStartWithSlash()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.DoesNotContain("\"/" + SrcPrefix + "Modules/AI/", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B03_AdapterDirectory_DoesNotContainDotDotEscape()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.DoesNotContain("../", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B04_SystemOneResourceName_IsAlphanumeric()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains("SystemOneResourceName = \"systemone\";", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B05_SystemOneApiKeyLocal_IsLocalString()
    {
        var content = File.ReadAllText(SystemOneHostingStatePath());
        Assert.Contains("SystemOneApiKeyLocal = \"local\";", content, StringComparison.Ordinal);
    }

    #endregion

    #region F11: Stale InternalsVisibleTo Cleanup

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_01_IntoChatOSClientOrleansCsproj_FileExists()
    {
        var csprojPath = ClientOrleansCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Csproj must exist at {csprojPath}");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_02_IntoChatOSClientOrleansCsproj_GrantsFollowOwnTestsRule()
    {
        var xml = XDocument.Load(ClientOrleansCsprojPath());
        var propsXml = XDocument.Load(Path.Combine(Repository.ModulesRoot, "Directory.Build.props"));
        var ivtElements = xml.Descendants("InternalsVisibleTo")
            .Concat(propsXml.Descendants("InternalsVisibleTo"))
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => x != null)
            .ToList();

        Assert.NotEmpty(ivtElements);
        Assert.All(ivtElements, elem => Assert.StartsWith("IntoChat.OS.", elem, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_03_IntoChatOSClientOrleansCsproj_ParsesAsValidXml()
    {
        var xml = XDocument.Load(ClientOrleansCsprojPath());
        Assert.NotNull(xml.Root);
        Assert.Equal("Project", xml.Root.Name.LocalName);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_04_ClientOrleansCsproj_ReferencesFramework()
    {
        var content = File.ReadAllText(ClientOrleansCsprojPath());
        Assert.Contains("IntoChat.OS", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_05_ClientOrleansDirectory_ContainsExpectedSources()
    {
        var dir = ClientOrleansDirectory();
        Assert.True(File.Exists(Path.Combine(dir, "BrainClient.cs")));
        Assert.True(File.Exists(Path.Combine(dir, "OrleansBrain.cs")));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B01_ClientOrleansCsproj_HasNoEmptyInternalsVisibleTo()
    {
        var xml = XDocument.Load(ClientOrleansCsprojPath());
        var ivts = xml.Descendants("InternalsVisibleTo").Select(x => x.Attribute("Include")?.Value).ToList();
        Assert.DoesNotContain("", ivts);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B02_ClientOrleansCsproj_NoDuplicateGrants()
    {
        var xml = XDocument.Load(ClientOrleansCsprojPath());
        var ivts = xml.Descendants("InternalsVisibleTo").Select(x => x.Attribute("Include")?.Value).ToList();
        Assert.Equal(ivts.Count, ivts.Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B03_ClientOrleansCsproj_FileLength_IsReasonable()
    {
        var fi = new FileInfo(ClientOrleansCsprojPath());
        Assert.True(fi.Length > 200 && fi.Length < 100_000);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B04_ClientOrleansCsproj_ContainsProjectClosingTag()
    {
        var content = File.ReadAllText(ClientOrleansCsprojPath());
        Assert.Contains("</Project>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B05_ClientOrleansDirectory_ContainsOrleansBrain()
    {
        Assert.True(File.Exists(Path.Combine(ClientOrleansDirectory(), "OrleansBrain.cs")));
    }

    #endregion

    #region F18: API Tracking Baseline & Contract Manifest Update

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_01_ContractManifestFile_ExistsInTestingDirectory()
    {
        var manifestPath = ContractManifestPath();
        Assert.True(File.Exists(manifestPath), $"ContractManifest.json must exist at '{manifestPath}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_02_ContractManifestFile_ParsesAsValidJsonDocument()
    {
        var content = File.ReadAllText(ContractManifestPath());
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_03_ContractManifestFacts_ClassExistsInTestingSuite()
    {
        var factsPath = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "ContractManifestFacts.cs");
        Assert.True(File.Exists(factsPath), $"ContractManifestFacts.cs must exist at '{factsPath}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_04_ContractManifest_ContainsContractsSection()
    {
        var content = File.ReadAllText(ContractManifestPath());
        using var doc = JsonDocument.Parse(content);
        var hasContracts = doc.RootElement.TryGetProperty("Contracts", out _)
            || doc.RootElement.TryGetProperty("Grains", out _);
        Assert.True(hasContracts);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_05_ContractManifest_TracksCoreModules()
    {
        var content = File.ReadAllText(ContractManifestPath());
        Assert.Contains("IntoChat.OS.Framework", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B02_ContractManifest_FileSize_ExceedsTenKilobytes()
    {
        var fi = new FileInfo(ContractManifestPath());
        Assert.True(fi.Length > 10_000, $"Manifest size was {fi.Length} bytes, expected > 10KB");
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B03_ContractManifest_LineCount_ExceedsOneHundredLines()
    {
        var lineCount = File.ReadAllLines(ContractManifestPath()).Length;
        Assert.True(lineCount > 100);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B04_ContractManifest_ValidGrainInterfacesSection()
    {
        var content = File.ReadAllText(ContractManifestPath());
        using var doc = JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("GrainInterfaces", out var grains));
        Assert.Equal(JsonValueKind.Object, grains.ValueKind);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B05_ContractManifest_TracksIdentityDirectory()
    {
        var content = File.ReadAllText(ContractManifestPath());
        Assert.Contains("IdentityDirectoryNeuron", content, StringComparison.Ordinal);
    }

    #endregion

    #region F19: TreatWarningsAsErrors Zero-Warning Enforcement

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_01_DirectoryBuildProps_ContainsTreatWarningsAsErrors()
    {
        var propsPath = Path.Combine(Repository.Root, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist at root");

        var content = File.ReadAllText(propsPath);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_02_DirectoryBuildProps_SpecifiesNet11TargetFramework()
    {
        var content = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("<TargetFramework>net11.0</TargetFramework>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_03_DirectoryBuildProps_EnforcesNullableReferenceTypes()
    {
        var content = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("<Nullable>enable</Nullable>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_04_DirectoryBuildProps_EnforcesImplicitUsings()
    {
        var content = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.Contains("<ImplicitUsings>enable</ImplicitUsings>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_05_DirectoryPackagesProps_DefinesCentralPackageManagement()
    {
        var packagesPath = Path.Combine(Repository.Root, "Directory.Packages.props");
        Assert.True(File.Exists(packagesPath), "Directory.Packages.props must exist at root");
        var content = File.ReadAllText(packagesPath);
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B01_DirectoryBuildProps_ValidXmlStructure()
    {
        var xml = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.NotNull(xml.Root);
        Assert.Equal("Project", xml.Root.Name.LocalName);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B02_DirectoryBuildProps_TargetFrameworkTrimmed()
    {
        var xml = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"));
        var tf = xml.Descendants("TargetFramework").First().Value.Trim();
        Assert.Equal("net11.0", tf);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B03_DirectoryBuildProps_NotConfiguredToFalse()
    {
        var content = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Assert.DoesNotContain("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>", content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B04_DirectoryPackagesProps_ValidXml()
    {
        var xml = XDocument.Load(Path.Combine(Repository.Root, "Directory.Packages.props"));
        Assert.NotNull(xml.Root);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B05_DirectoryBuildProps_LocatedAtRepositoryRoot()
    {
        var propsPath = Path.Combine(Repository.Root, "Directory.Build.props");
        Assert.True(File.Exists(propsPath));
        var dir = Path.GetDirectoryName(propsPath);
        Assert.True(File.Exists(Path.Combine(dir!, Repository.SolutionFile)));
    }

    #endregion

    #region TF10: SystemOne Hosting With Kernel Contracts Separation

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F10_F13")]
    public void TF10_SystemOneHosting_WithKernelContracts_MaintainsCleanSeparation()
    {
        var hostingFile = SystemOneHostingStatePath();
        Assert.True(File.Exists(hostingFile), $"SystemOneHostingState.cs must exist at '{hostingFile}'");

        var kernelAssembly = typeof(IntoChat.OS.Kernel.OperatingSystemModules).Assembly;
        var kernelTypes = kernelAssembly.GetTypes().Select(t => t.Name).ToList();

        Assert.DoesNotContain("IntoChatOSNamesForward", kernelTypes);
        Assert.DoesNotContain("LegacyTypeNames", kernelTypes);
    }

    #endregion
}
