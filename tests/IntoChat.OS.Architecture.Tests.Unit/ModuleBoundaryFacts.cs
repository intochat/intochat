using System.Text.Json;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ModuleBoundaryFacts
{
    private static readonly string[] ForbiddenProjectPrefixes =
    [
        "IntoChat.OS.Kernel",
        "IntoChat.OS.Aspire",
    ];

    [Fact]
    public void PlatformModulesUnderSrcModulesNeverReferenceOsKernelOrAspireHost()
    {
        var moduleCsprojFiles = Repository.ModuleRootSourceFiles("*.csproj")
            .ToList();

        Assert.NotEmpty(moduleCsprojFiles);

        var offenders = new List<string>();
        foreach (var csprojPath in moduleCsprojFiles)
        {
            var doc = XDocument.Load(csprojPath);
            var projectReferences = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v is not null)
                .Select(v => Path.GetFileNameWithoutExtension(v!))
                .ToList();

            var packageReferences = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>()
                .ToList();

            var allReferences = projectReferences.Concat(packageReferences);
            foreach (var reference in allReferences)
            {
                if (ForbiddenProjectPrefixes.Any(prefix => reference.Equals(prefix, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{Repository.Relative(csprojPath)} -> {reference}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Modules under the modules root must never reference IntoChat.OS.Kernel or IntoChat.OS.Aspire.*:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoModuleClosureReachesAnOsKernelAssembly()
    {
        var projects = Repository.ModuleRootSourceFiles("*.csproj")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(projects);

        var offenders = new List<string>();
        foreach (var csprojPath in projects)
        {
            var assetsPath = ResolveAssetsPath(csprojPath);
            if (!File.Exists(assetsPath))
            {
                continue; // Assets might not exist before restore; build step will restore them
            }

            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            {
                continue;
            }

            var osPackages = new List<string>();
            foreach (var library in libraries.EnumerateObject())
            {
                var slash = library.Name.IndexOf('/', StringComparison.Ordinal);
                var name = slash < 0 ? library.Name : library.Name[..slash];

                if (ForbiddenProjectPrefixes.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    osPackages.Add(name);
                }
            }

            if (osPackages.Count > 0)
            {
                offenders.Add($"{Path.GetFileNameWithoutExtension(csprojPath)}: {string.Join(", ", osPackages.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))}");
            }
        }

        Assert.True(offenders.Count == 0, "Module restore closure reaches forbidden IntoChat.OS packages:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void PlatformModulesNeverReferenceAComponentImplementation()
    {
        var offenders = InboundComponentReferenceFindings(Repository.ModulesRoot, Repository.ComponentsRoot);
        Assert.True(
            offenders.Count == 0,
            "Modules under src/Modules must never reference a component implementation project (reference its *.Contracts project instead):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void InboundComponentReferenceChallengerFlagsModuleToComponentImplementation()
    {
        var root = Directory.CreateTempSubdirectory("module-boundary-sample-").FullName;
        try
        {
            var modules = Path.Combine(root, "Modules");
            var components = Path.Combine(root, "Components");
            Write(Path.Combine(components, "Beta", "IntoChat.Beta.csproj"), "<Project />");
            Write(Path.Combine(components, "Beta", "Contracts", "IntoChat.Beta.Contracts.csproj"), "<Project />");
            Write(Path.Combine(modules, "Clean", "IntoChat.OS.Modules.Clean.csproj"), """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="../../Components/Beta/Contracts/IntoChat.Beta.Contracts.csproj" />
                    <PackageReference Include="IntoChat.OS.Components.Beta.Contracts" />
                  </ItemGroup>
                </Project>
                """);
            Write(Path.Combine(modules, "Dirty", "IntoChat.OS.Modules.Dirty.csproj"), """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="../../Components/Beta/IntoChat.Beta.csproj" />
                    <PackageReference Include="IntoChat.OS.Components.Beta" />
                  </ItemGroup>
                </Project>
                """);

            var offenders = InboundComponentReferenceFindings(modules, components);

            Assert.Equal(2, offenders.Count);
            Assert.Contains(offenders, finding => finding.Contains("IntoChat.OS.Modules.Dirty.csproj -> IntoChat.Beta", StringComparison.Ordinal));
            Assert.Contains(offenders, finding => finding.Contains("IntoChat.OS.Modules.Dirty.csproj -> IntoChat.OS.Components.Beta", StringComparison.Ordinal));
            Assert.DoesNotContain(offenders, finding => finding.Contains("Clean", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    internal static List<string> InboundComponentReferenceFindings(string modulesRoot, string componentsRoot)
    {
        var offenders = new List<string>();
        if (!Directory.Exists(modulesRoot) || !Directory.Exists(componentsRoot))
        {
            return offenders;
        }

        var componentImplementations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(componentsRoot))
        {
            var name = Path.GetFileName(directory);
            if (name is null or "bin" or "obj")
            {
                continue;
            }

            componentImplementations.Add($"IntoChat.{name}");
            componentImplementations.Add($"IntoChat.OS.Components.{name}");
        }

        if (componentImplementations.Count == 0)
        {
            return offenders;
        }

        foreach (var csprojPath in Repository.SourceFiles(modulesRoot, "*.csproj").Order(StringComparer.Ordinal))
        {
            var doc = XDocument.Load(csprojPath);
            var projectReferences = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(v => Path.GetFileNameWithoutExtension(v.Replace('\\', '/')))
                .OfType<string>();

            var packageReferences = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>();

            foreach (var reference in projectReferences.Concat(packageReferences))
            {
                if (componentImplementations.Contains(reference))
                {
                    var label = Path.GetRelativePath(modulesRoot, csprojPath).Replace('\\', '/');
                    offenders.Add($"{label} -> {reference}");
                }
            }
        }

        return offenders;
    }

    private static string ResolveAssetsPath(string csprojPath)
    {
        var artifactsAssets = Path.Combine(
            Repository.Root,
            "artifacts",
            "obj",
            Path.GetFileNameWithoutExtension(csprojPath),
            "project.assets.json");
        if (File.Exists(artifactsAssets))
        {
            return artifactsAssets;
        }

        return Path.Combine(Path.GetDirectoryName(csprojPath)!, "obj", "project.assets.json");
    }
}
