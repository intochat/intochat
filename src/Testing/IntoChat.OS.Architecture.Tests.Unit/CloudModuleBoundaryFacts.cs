using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// R4-01, D-01 (09-05): every IntoChat.Cloud.* project is built on DigitalBrain, os contracts and third-party packages only, so
// the cloud modules never depend on the kernel's internals. No baseline file: the set of offenders is empty and stays empty.
// Allowed references of a non-test project under src/IntoChat.Cloud:
//   DigitalBrain / DigitalBrain.*, IntoChat.OS.*.Contracts, IntoChat.Cloud.*.Contracts, a project in the same Modules/<Name>/ folder,
//   IntoChat.Cloud.Localization(.Generator), and a package whose id does not start with "IntoChat.".
public sealed class CloudModuleBoundaryFacts
{
    private static readonly string[] InternalNamespaces = ["Authority", "Framework", "Kernel", "Sdk"];

    // The folder that holds the cloud modules, learned from the solution's IntoChat.Cloud.* projects.
    private static string CloudDirectory
    {
        get
        {
            var marker = Path.DirectorySeparatorChar + "IntoChat.Cloud" + Path.DirectorySeparatorChar;
            var project = Repository.Projects.Values.First(project => project.Directory.Contains(marker, StringComparison.Ordinal));
            return project.Directory[..(project.Directory.IndexOf(marker, StringComparison.Ordinal) + marker.Length - 1)];
        }
    }

    private static bool IsTest(string name) => name.EndsWith(".Tests", StringComparison.Ordinal) || name.Contains(".Tests.", StringComparison.Ordinal);

    // The offending references of one csproj (xml text). moduleReferences are the project names inside the same module folder.
    internal static string[] Violations(string csprojXml, IReadOnlySet<string> moduleProjects, Func<string, string?> projectNameOf)
    {
        var document = XDocument.Parse(csprojXml);
        var offenders = new List<string>();
        foreach (var include in document.Descendants("ProjectReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>())
        {
            var name = projectNameOf(include) ?? Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            if (!AllowedProject(name, moduleProjects)) { offenders.Add("project " + name); }
        }
        foreach (var package in document.Descendants("PackageReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>())
        {
            if (package.StartsWith("IntoChat.", StringComparison.Ordinal) && !AllowedProject(package, moduleProjects)) { offenders.Add("package " + package); }
        }
        return [.. offenders];
    }

    private static bool AllowedProject(string name, IReadOnlySet<string> moduleProjects)
        => name is "DigitalBrain" || name.StartsWith("DigitalBrain.", StringComparison.Ordinal)
            || (name.StartsWith("IntoChat.OS.", StringComparison.Ordinal) && name.EndsWith(".Contracts", StringComparison.Ordinal))
            || (name.StartsWith("IntoChat.Cloud.", StringComparison.Ordinal) && name.EndsWith(".Contracts", StringComparison.Ordinal))
            || name is "IntoChat.Cloud.Localization" or "IntoChat.Cloud.Localization.Generator" or "IntoChat.Cloud.Mail.Templates"
            || moduleProjects.Contains(name);

    private static IEnumerable<(string Name, string Path, string Module)> CloudProjects()
        => Directory.EnumerateFiles(CloudDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Select(path =>
            {
                var relative = Path.GetRelativePath(Path.Combine(CloudDirectory, "Modules"), path).Replace('\\', '/');
                return (Path.GetFileNameWithoutExtension(path), path, relative.Split('/')[0]);
            });

    [Fact]
    public void CloudProjectsReferenceOnlyDigitalBrainContractsAndTheirOwnModule()
    {
        var all = CloudProjects().ToArray();
        var offenders = new List<string>();
        foreach (var project in all.Where(project => !IsTest(project.Name)))
        {
            var sameModule = all.Where(other => other.Module == project.Module).Select(other => other.Name).ToHashSet(StringComparer.Ordinal);
            offenders.AddRange(Violations(File.ReadAllText(project.Path), sameModule, include => null)
                .Select(violation => Repository.Relative(project.Path) + ": " + violation));
        }
        Assert.True(offenders.Count == 0, "IntoChat.Cloud projects may not reference os internals:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CloudSourcesNeverUseOsInternalNamespaces()
    {
        var offenders = new List<string>();
        foreach (var project in CloudProjects().Where(project => !IsTest(project.Name)))
        {
            foreach (var file in Repository.SourceFiles(Path.GetDirectoryName(project.Path)!, "*.cs"))
            {
                var text = File.ReadAllText(file);
                offenders.AddRange(InternalNamespaces
                    .Where(ns => text.Contains("using IntoChat.OS." + ns, StringComparison.Ordinal))
                    .Select(ns => Repository.Relative(file) + ": using IntoChat.OS." + ns));
            }
        }
        Assert.True(offenders.Count == 0, "IntoChat.Cloud sources may not use os internal namespaces:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CloudHoldsNoResidueAndNoPrivateDeploymentValues()
    {
        var offenders = Directory.EnumerateFiles(CloudDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Select(Repository.Relative)
            .Where(path => path.Contains("TripRadar", StringComparison.OrdinalIgnoreCase) || path.Contains("DeploymentKit", StringComparison.OrdinalIgnoreCase)
                || (path.Contains("/deployment/", StringComparison.Ordinal) && Path.GetFileName(path) is "test.json" or "prod.json"))
            .ToArray();
        Assert.True(offenders.Length == 0, "IntoChat.Cloud may not carry cross-product residue or private deployment values:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheCheckerFlagsOsInternalsAndAllowsTheDocumentedSet()
    {
        var module = new HashSet<string>(["IntoChat.Cloud.Auth", "IntoChat.Cloud.Auth.Core"], StringComparer.Ordinal);
        const string Clean = """
            <Project><ItemGroup>
              <ProjectReference Include="../../../../../framework/src/DigitalBrain/DigitalBrain.csproj" />
              <ProjectReference Include="../../Contracts/IntoChat.Cloud.Auth.Contracts/IntoChat.Cloud.Auth.Contracts.csproj" />
              <ProjectReference Include="../IntoChat.Cloud.Auth.Core/IntoChat.Cloud.Auth.Core.csproj" />
              <ProjectReference Include="../../../../IntoChat.OS.Modules/IntoChat.OS/Circles/Contracts/IntoChat.OS.Modules.Circles.Contracts/IntoChat.OS.Modules.Circles.Contracts.csproj" />
              <PackageReference Include="Otp.NET" />
            </ItemGroup></Project>
            """;
        Assert.Empty(Violations(Clean, module, _ => null));
        const string Dirty = """
            <Project><ItemGroup>
              <PackageReference Include="IntoChat.OS.Framework" />
              <ProjectReference Include="../../../../IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj" />
              <ProjectReference Include="../../../Mail/src/IntoChat.Cloud.Mail/IntoChat.Cloud.Mail.csproj" />
            </ItemGroup></Project>
            """;
        Assert.Equal(["project IntoChat.OS.Kernel", "project IntoChat.Cloud.Mail", "package IntoChat.OS.Framework"],
            Violations(Dirty, module, _ => null).OrderBy(v => v.StartsWith("package", StringComparison.Ordinal)).ToArray());
    }
}
