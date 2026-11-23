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

    // The Aspire component's packages carry IntoChat.OS ids (PackageRootAllowlistFacts) though they are not the Aspire host;
    // they are the component's own projects (IntoChat.Aspire, IntoChat.Aspire.Contracts), so a closure may hold them.
    private static readonly string[] ComponentPackages =
    [
        "IntoChat.OS.Aspire",
        "IntoChat.OS.Aspire.Contracts",
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

                if (!ComponentPackages.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && ForbiddenProjectPrefixes.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
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
