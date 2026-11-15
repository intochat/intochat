using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1 Feature Coverage facts for Feature 1 (Central Packages 0.20.0 Bump),
/// Feature 3 (DigitalBrain.Simulation substitution), and Feature 6 (FrameworkBoundaryFacts parity).
/// </summary>
public sealed class PackageRestoreFacts : E2ETestBase
{
    [Fact]
    public void T1_F01_TC01_CentralPackagesProps_AllDigitalBrainPackagesPinnedTo0200()
    {
        var doc = XDocument.Load(PackagesPropsPath);
        var dbPackages = doc.Descendants("PackageVersion")
            .Where(x => (x.Attribute("Include")?.Value ?? string.Empty).StartsWith("DigitalBrain", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(dbPackages);
        Assert.True(dbPackages.Count >= 17, $"Expected at least 17 DigitalBrain packages, found {dbPackages.Count}.");

        var non0220Offenders = dbPackages
            .Where(x => x.Attribute("Version")?.Value != "0.22.0")
            .Select(x => $"{x.Attribute("Include")?.Value}: {x.Attribute("Version")?.Value}")
            .ToList();

        Assert.True(non0220Offenders.Count == 0,
            "All DigitalBrain packages must be pinned to 0.22.0 in Directory.Packages.props:\n" + string.Join("\n", non0220Offenders));
    }

    [Fact]
    public void T1_F01_TC02_ZeroLegacy018Or019Versions_AcrossEntireRepository()
    {
        var regex = new Regex(@"\b0\.(?:18|19)\.[0-9a-zA-Z\.\-]+", RegexOptions.Compiled);
        var buildFiles = Directory.EnumerateFiles(RepoRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase));

        var offenders = new List<string>();
        foreach (var file in buildFiles)
        {
            var text = File.ReadAllText(file);
            var match = regex.Match(text);
            if (match.Success)
            {
                offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "No 0.18.* or 0.19.* packages allowed anywhere in repository:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F01_TC03_NoProjectLevelVersionOverrides_ForDigitalBrainPackages()
    {
        var offenders = new List<string>();
        foreach (var file in FindCsprojFiles())
        {
            var doc = XDocument.Load(file);
            var badRefs = doc.Descendants("PackageReference")
                .Where(p => (p.Attribute("Include")?.Value ?? string.Empty).StartsWith("DigitalBrain", StringComparison.Ordinal))
                .Where(p => p.Attribute("Version") != null || p.Attribute("VersionOverride") != null)
                .Select(p => $"{Path.GetRelativePath(RepoRoot, file)}: {p.Attribute("Include")?.Value}")
                .ToList();

            offenders.AddRange(badRefs);
        }

        Assert.True(offenders.Count == 0,
            "Projects must adhere to Central Package Management; no local Version/VersionOverride allowed:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F01_TC04_CentralPackageManagement_StrictConfigurationEnforced()
    {
        var doc = XDocument.Load(PackagesPropsPath);
        var cpmElement = doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault();
        Assert.NotNull(cpmElement);
        Assert.Equal("true", cpmElement.Value.Trim(), ignoreCase: true);
    }

    [Fact]
    public void T1_F01_TC05_DigitalBrainSimulation_CentrallyDeclaredAt0200()
    {
        var doc = XDocument.Load(PackagesPropsPath);
        var simPackage = doc.Descendants("PackageVersion")
            .FirstOrDefault(p => p.Attribute("Include")?.Value == "DigitalBrain.Simulation");

        Assert.NotNull(simPackage);
        Assert.Equal("0.22.0", simPackage.Attribute("Version")?.Value);
    }

    [Fact]
    public void T1_F01_TC06_Dx03PackagesListInFrameworkBoundaryFacts_Matches0200PackableSet()
    {
        var frameworkFactsFile = Path.Combine(RepoRoot, "tests", "IntoChat.OS.Architecture.Tests.Unit", "FrameworkBoundaryFacts.cs");
        Assert.True(File.Exists(frameworkFactsFile), "FrameworkBoundaryFacts.cs must exist.");

        var text = File.ReadAllText(frameworkFactsFile);
        Assert.Contains("\"DigitalBrain.Simulation\"", text);
        Assert.DoesNotContain("\"DigitalBrain.Client\"", text);
    }

    [Fact]
    public void T1_F01_TC07_CentralPackageTransitivePinning_Enforced()
    {
        var doc = XDocument.Load(PackagesPropsPath);
        var pinningElement = doc.Descendants("CentralPackageTransitivePinningEnabled").FirstOrDefault();
        Assert.NotNull(pinningElement);
        Assert.Equal("true", pinningElement.Value.Trim(), ignoreCase: true);
    }

    [Fact]
    public void T1_F03_TC01_AppsModuleCsproj_ReferencesDigitalBrainSimulation()
    {
        var appsProj = FindCsprojFiles().FirstOrDefault(f => f.EndsWith("IntoChat.OS.Modules.Catalog.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(appsProj);

        var doc = XDocument.Load(appsProj);
        var hasSim = doc.Descendants("PackageReference").Any(p => p.Attribute("Include")?.Value == "DigitalBrain.Simulation");
        Assert.True(hasSim, "IntoChat.OS.Modules.Catalog.csproj must reference DigitalBrain.Simulation.");
    }

    [Fact]
    public void T1_F03_TC02_AppsModuleCsproj_DoesNotReferenceDigitalBrainTesting()
    {
        var appsProj = FindCsprojFiles().FirstOrDefault(f => f.EndsWith("IntoChat.OS.Modules.Catalog.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(appsProj);

        var doc = XDocument.Load(appsProj);
        var hasTesting = doc.Descendants("PackageReference")
            .Any(p => p.Attribute("Include")?.Value is "DigitalBrain.Testing" or "DigitalBrain.Testing.Silo");
        Assert.False(hasTesting, "IntoChat.OS.Modules.Catalog.csproj must NOT reference DigitalBrain.Testing or DigitalBrain.Testing.Silo.");
    }

    [Fact]
    public void T1_F03_TC03_ZeroXUnitPackageReferences_InAnyProductionProject()
    {
        var offenders = new List<string>();
        foreach (var file in FindCsprojFiles())
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".Testing", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".Testing.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var doc = XDocument.Load(file);
            var xunitRefs = doc.Descendants("PackageReference")
                .Where(p => (p.Attribute("Include")?.Value ?? string.Empty).Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                            (p.Attribute("Include")?.Value ?? string.Empty).Contains("ArchUnitNET", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{Path.GetRelativePath(RepoRoot, file)}: {p.Attribute("Include")?.Value}")
                .ToList();

            offenders.AddRange(xunitRefs);
        }

        Assert.True(offenders.Count == 0,
            "Production projects must not reference xUnit or test frameworks:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F03_TC04_ZeroTestingHarnessReferences_InProductionProjects()
    {
        var offenders = new List<string>();
        foreach (var file in FindCsprojFiles())
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".Testing", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".Testing.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var doc = XDocument.Load(file);
            var badHarnessRefs = doc.Descendants("PackageReference")
                .Where(p => p.Attribute("Include")?.Value is "DigitalBrain.Testing" or "DigitalBrain.Testing.Silo" or "Microsoft.Orleans.TestingHost")
                .Select(p => $"{Path.GetRelativePath(RepoRoot, file)}: {p.Attribute("Include")?.Value}")
                .ToList();

            offenders.AddRange(badHarnessRefs);
        }

        Assert.True(offenders.Count == 0,
            "Production projects must not reference testing harnesses:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F03_TC05_DigitalBrainSimulationAssembly_HasZeroXUnitAndOrleansDependencies()
    {
        var simAssembly = typeof(TestOS).Assembly;
        var referencedAssemblies = simAssembly.GetReferencedAssemblies();

        var xunitRefs = referencedAssemblies.Where(a => a.Name?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var orleansRefs = referencedAssemblies.Where(a => a.Name?.Contains("Orleans", StringComparison.OrdinalIgnoreCase) == true).ToList();

        Assert.Empty(xunitRefs);
        Assert.Empty(orleansRefs);
    }
}
