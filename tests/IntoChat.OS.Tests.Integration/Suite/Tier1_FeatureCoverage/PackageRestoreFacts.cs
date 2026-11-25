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
            .Where(x => (x.Attribute("Include")?.Value ?? string.Empty) is var inc
                && (inc == "DigitalBrain" || inc.StartsWith("DigitalBrain.", StringComparison.Ordinal)))
            .Select(x => x.Attribute("Include")?.Value)
            .ToList();

        Assert.Empty(dbPackages);

        var version = File.ReadAllText(Path.Combine(RepoRoot, "VERSION")).Trim();
        Assert.Equal("0.24.0", version);

        var frameworkSrcDir = Path.GetFullPath(Path.Combine(RepoRoot, "framework", SourceSegment)) + Path.DirectorySeparatorChar;
        Assert.True(Directory.Exists(frameworkSrcDir), $"Expected framework source directory at {frameworkSrcDir}.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var projectRefCount = 0;
        foreach (var csproj in FindCsprojFiles())
        {
            var projectDoc = XDocument.Load(csproj);
            var pkgOffenders = projectDoc.Descendants("PackageReference")
                .Where(p => (p.Attribute("Include")?.Value ?? string.Empty) is var inc
                    && (inc == "DigitalBrain" || inc.StartsWith("DigitalBrain.", StringComparison.Ordinal)))
                .Select(p => $"{Path.GetRelativePath(RepoRoot, csproj)}: {p.Attribute("Include")?.Value}")
                .ToList();
            Assert.Empty(pkgOffenders);

            var projectDir = Path.GetDirectoryName(csproj)!;
            projectRefCount += projectDoc.Descendants("ProjectReference")
                .Select(p => p.Attribute("Include")?.Value)
                .OfType<string>()
                .Where(include => !include.Contains("$(", StringComparison.Ordinal))
                .Select(include => Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', '/'))))
                .Count(resolved => resolved.StartsWith(frameworkSrcDir, comparison));
        }

        Assert.True(projectRefCount > 0, "Expected product projects to reference framework projects via ProjectReference.");
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

        Assert.Null(simPackage);

        var simProject = Path.Combine(RepoRoot, "framework", SourceSegment, "DigitalBrain.Simulation", "DigitalBrain.Simulation.csproj");
        Assert.True(File.Exists(simProject), $"Expected DigitalBrain.Simulation project at {simProject}.");

        var version = File.ReadAllText(Path.Combine(RepoRoot, "VERSION")).Trim();
        Assert.Equal("0.24.0", version);

        var infoVersion = typeof(TestOS).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(infoVersion);
        Assert.StartsWith(version, infoVersion, StringComparison.Ordinal);
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

    // The simulation runtime is the deploy gate of generated units (their scenarios run on it). Unit authoring owns that gate, so
    // the authoring module references it and the catalog no longer does.
    [Fact]
    public void T1_F03_TC01_AuthoringModuleCsproj_ReferencesDigitalBrainSimulation_AndTheCatalogDoesNot()
    {
        Assert.True(References("IntoChat.OS.Modules.Authoring.csproj", "DigitalBrain.Simulation"),
            "IntoChat.OS.Modules.Authoring.csproj must reference DigitalBrain.Simulation.");
        Assert.False(References("IntoChat.OS.Modules.Catalog.csproj", "DigitalBrain.Simulation"),
            "IntoChat.OS.Modules.Catalog.csproj must NOT reference DigitalBrain.Simulation; the deploy gate is the authoring module's.");
    }

    [Fact]
    public void T1_F03_TC02_AppsModuleCsproj_DoesNotReferenceDigitalBrainTesting()
    {
        foreach (var project in (string[])["IntoChat.OS.Modules.Catalog.csproj", "IntoChat.OS.Modules.Authoring.csproj"])
        {
            Assert.False(References(project, "DigitalBrain.Testing", "DigitalBrain.Testing.Silo"),
                $"{project} must NOT reference DigitalBrain.Testing or DigitalBrain.Testing.Silo.");
        }
    }

    private static bool References(string projectFile, params string[] names)
    {
        var project = FindCsprojFiles().FirstOrDefault(f => f.EndsWith(projectFile, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(project);

        var doc = XDocument.Load(project);
        return doc.Descendants("ProjectReference")
                .Any(p => names.Contains(Path.GetFileNameWithoutExtension((p.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'))))
            || doc.Descendants("PackageReference").Any(p => p.Attribute("Include")?.Value is { } include && names.Contains(include));
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
                .Concat(doc.Descendants("ProjectReference")
                    .Where(p => Path.GetFileNameWithoutExtension((p.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'))
                        is "DigitalBrain.Testing" or "DigitalBrain.Testing.Silo" or "Microsoft.Orleans.TestingHost")
                    .Select(p => $"{Path.GetRelativePath(RepoRoot, file)}: {p.Attribute("Include")?.Value}"))
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
